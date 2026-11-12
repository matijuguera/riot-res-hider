using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LolModoCine
{
    /// <summary>
    /// El modo cine en si: mira que ventana tiene el foco y, si es de un juego, oculta la barra
    /// de tareas, los iconos del escritorio y pone un fondo negro detras. Al salir del juego
    /// devuelve todo.
    /// </summary>
    sealed class Engine : IDisposable
    {
        const int PollIntervalMs = 500;
        // Mientras arranca, el juego puede pisar el tamano un par de veces; se reintenta durante
        // este rato y despues no se lo molesta mas, por si lo redimensionas a mano.
        const int ResizeGraceTicks = 120;  // ~60 s

        // Si el proceso muere con el modo cine activo, la barra queda oculta. Este archivo
        // existe solo mientras hay algo oculto, para devolverlo en el proximo arranque.
        static readonly string FlagPath = Path.Combine(Config.Dir, "restore.flag");

        readonly Config cfg;
        readonly Backdrop backdrop = new Backdrop();
        readonly Timer timer = new Timer();
        readonly Native.WinEventProc hookProc;  // referencia viva: el hook guarda un puntero
        IntPtr hook;

        HashSet<string> games;
        IntPtr iconHwnd;
        IntPtr taskbarHwnd;
        bool hidTaskbar;
        bool hidIcons;

        uint lastPid;
        string lastName = "";

        IntPtr resizeHwnd;
        int resizeLeft;

        public bool Active { get; private set; }
        public event Action StateChanged;

        public Engine(Config cfg)
        {
            this.cfg = cfg;
            games = cfg.Games();
            BuildCache();
            RecoverFromCrash();

            // El evento de cambio de foco hace la transicion al instante; el timer queda de
            // respaldo y lleva la cuenta del periodo de gracia del tamano de ventana.
            hookProc = (h, evt, hwnd, idObject, idChild, thread, time) => Poll(false);
            hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

            timer.Interval = PollIntervalMs;
            timer.Tick += (s, e) => Poll(true);
            timer.Start();
        }

        /// <summary>La configuracion cambio: se vuelve a aplicar desde cero con los valores nuevos.</summary>
        public void Refresh()
        {
            games = cfg.Games();
            resizeHwnd = IntPtr.Zero;
            if (Active) Deactivate();
            Poll(false);
        }

        /// <summary>Explorer se reinicio: las ventanas cacheadas ya no existen y las nuevas nacen visibles.</summary>
        public void ExplorerRestarted()
        {
            BuildCache();
            hidTaskbar = hidIcons = false;
            if (Active)
            {
                Active = false;
                Poll(false);
            }
        }

        void BuildCache()
        {
            IntPtr defView = IntPtr.Zero;
            IntPtr progman = Native.FindWindowW("Progman", null);
            if (progman != IntPtr.Zero)
                defView = Native.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

            if (defView == IntPtr.Zero)
            {
                // Con fondos animados o presentacion de diapositivas los iconos cuelgan de un WorkerW.
                Native.EnumWindows((hwnd, _) =>
                {
                    IntPtr h = Native.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (h == IntPtr.Zero) return true;
                    defView = h;
                    return false;
                }, IntPtr.Zero);
            }

            iconHwnd = defView != IntPtr.Zero
                ? Native.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null)
                : IntPtr.Zero;
            // Solo la barra del monitor principal; las de monitores secundarios no se tocan.
            taskbarHwnd = Native.FindWindowW("Shell_TrayWnd", null);
        }

        void RecoverFromCrash()
        {
            string what;
            try
            {
                what = File.ReadAllText(FlagPath);
            }
            catch (Exception)
            {
                return;  // lo normal: la ultima vez cerro bien
            }
            if (what.Contains("taskbar") && taskbarHwnd != IntPtr.Zero)
                Native.ShowWindow(taskbarHwnd, Native.SW_SHOW);
            if (what.Contains("icons") && iconHwnd != IntPtr.Zero)
                Native.ShowWindow(iconHwnd, Native.SW_SHOW);
            ClearFlag();
        }

        /// <summary>Nombre del exe duenio de esa ventana, en minusculas. Cacheado por PID.</summary>
        string ProcessName(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == lastPid) return lastName;

            lastPid = pid;
            lastName = "";
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return lastName;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (Native.QueryFullProcessImageNameW(process, 0, buffer, ref size))
                    lastName = Path.GetFileName(buffer.ToString()).ToLowerInvariant();
            }
            finally
            {
                Native.CloseHandle(process);
            }
            return lastName;
        }

        /// <param name="tick">true cuando viene del timer, false cuando viene de un cambio de foco.</param>
        void Poll(bool tick)
        {
            if (cfg.Paused)
            {
                if (Active) Deactivate();
                return;
            }

            IntPtr hwnd = Native.GetForegroundWindow();
            // En pleno cambio de ventana puede no haber ninguna en foco por un instante; solo el
            // timer lo toma como "sin juego", para no parpadear.
            if (hwnd == IntPtr.Zero && !tick) return;

            string name = hwnd != IntPtr.Zero ? ProcessName(hwnd) : "";
            if (games.Contains(name))
            {
                if (tick) KeepWindowSize(hwnd, name);
                if (!Active) Activate();
                if (cfg.BlackBackground) backdrop.PlaceBehind(hwnd);
                else backdrop.Hide();
            }
            else if (Active)
            {
                Deactivate();
            }
        }

        void Activate()
        {
            if (!Native.IsWindow(taskbarHwnd) || !Native.IsWindow(iconHwnd)) BuildCache();

            // Solo se oculta lo que estaba visible, asi al volver no se muestra algo que el
            // usuario ya tenia escondido (por ejemplo los iconos del escritorio).
            if (cfg.HideTaskbar && Native.IsWindowVisible(taskbarHwnd))
            {
                Native.ShowWindow(taskbarHwnd, Native.SW_HIDE);
                hidTaskbar = true;
            }
            if (cfg.HideIcons && Native.IsWindowVisible(iconHwnd))
            {
                Native.ShowWindow(iconHwnd, Native.SW_HIDE);
                hidIcons = true;
            }
            WriteFlag();

            Active = true;
            if (StateChanged != null) StateChanged();
        }

        void Deactivate()
        {
            backdrop.Hide();
            if (hidTaskbar) Native.ShowWindow(taskbarHwnd, Native.SW_SHOW);
            if (hidIcons) Native.ShowWindow(iconHwnd, Native.SW_SHOW);
            hidTaskbar = hidIcons = false;
            ClearFlag();

            Active = false;
            if (StateChanged != null) StateChanged();
        }

        void WriteFlag()
        {
            if (!hidTaskbar && !hidIcons) return;
            try
            {
                Directory.CreateDirectory(Config.Dir);
                File.WriteAllText(FlagPath, (hidTaskbar ? "taskbar " : "") + (hidIcons ? "icons" : ""));
            }
            catch (Exception)
            {
                // Sin el archivo solo se pierde la recuperacion tras un cierre forzado.
            }
        }

        static void ClearFlag()
        {
            try
            {
                File.Delete(FlagPath);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Le fija a la ventana de TFT el area de juego pedida y la centra.</summary>
        void KeepWindowSize(IntPtr hwnd, string name)
        {
            // TFT solo acepta resoluciones que el monitor declare como modos reales, asi que
            // fijarle el tamano a la ventana es la unica forma de tener una medida propia. En
            // modo ventana Unreal renderiza al tamano de su ventana, asi que el juego sigue.
            if (!cfg.PinTft || name != Config.TftGame || cfg.Width <= 0 || cfg.Height <= 0) return;

            if (hwnd != resizeHwnd)  // ventana nueva: arranca de nuevo el periodo de gracia
            {
                resizeHwnd = hwnd;
                resizeLeft = ResizeGraceTicks;
            }
            if (resizeLeft <= 0) return;
            resizeLeft--;

            Native.RECT client;
            Native.GetClientRect(hwnd, out client);
            if (client.Right == cfg.Width && client.Bottom == cfg.Height) return;
            if (client.Right < 200 || client.Bottom < 200) return;  // todavia arrancando

            if (Native.IsZoomed(hwnd))  // maximizada: SetWindowPos no la achica
            {
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                Native.GetClientRect(hwnd, out client);
            }

            Native.RECT window;
            Native.GetWindowRect(hwnd, out window);
            int totalW = cfg.Width + (window.Right - window.Left) - client.Right;
            int totalH = cfg.Height + (window.Bottom - window.Top) - client.Bottom;

            Native.RECT screen;
            if (!MonitorRect(hwnd, out screen)) return;
            int x = screen.Left + ((screen.Right - screen.Left) - totalW) / 2;
            int y = screen.Top + ((screen.Bottom - screen.Top) - totalH) / 2;

            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, totalW, totalH,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        public static bool MonitorRect(IntPtr hwnd, out Native.RECT rect)
        {
            var info = new Native.MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            bool ok = Native.GetMonitorInfoW(
                Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST), ref info);
            rect = info.rcMonitor;
            return ok;
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(hook);
                hook = IntPtr.Zero;
            }
            if (Active) Deactivate();
            backdrop.DestroyHandle();
        }
    }

    /// <summary>
    /// Ventana negra del tamano del monitor, pegada justo detras del juego. Tapa el fondo de
    /// pantalla y las ventanas que queden atras sin cambiar el fondo real (que tarda, y no se
    /// puede con fondos animados). Nunca toma el foco, asi que un click afuera del juego no lo saca.
    /// </summary>
    sealed class Backdrop : NativeWindow
    {
        bool visible;

        public void PlaceBehind(IntPtr game)
        {
            Native.RECT screen;
            if (!Engine.MonitorRect(game, out screen)) return;

            if (Handle == IntPtr.Zero)
            {
                CreateHandle(new CreateParams
                {
                    Caption = "",
                    Style = Native.WS_POPUP,
                    ExStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                });
            }

            // insertAfter = ventana del juego: queda inmediatamente detras, en su mismo monitor.
            Native.SetWindowPos(Handle, game, screen.Left, screen.Top,
                screen.Right - screen.Left, screen.Bottom - screen.Top,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            visible = true;
        }

        public void Hide()
        {
            if (!visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            visible = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_ERASEBKGND)
            {
                Native.RECT rect;
                Native.GetClientRect(m.HWnd, out rect);
                Native.FillRect(m.WParam, ref rect, Native.GetStockObject(Native.BLACK_BRUSH));
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)Native.MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }
    }
}

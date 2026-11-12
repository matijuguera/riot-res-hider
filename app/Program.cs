using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LolModoCine
{
    static class Program
    {
        public const string AppName = "LoL modo cine";

        // Una sola instancia: dos a la vez se pelearian, una ocultando y la otra mostrando.
        // "Local\" = por sesion de usuario, que es lo que corresponde para una app de bandeja.
        const string InstanceMutex = "Local\\LolModoCineApp";
        // El que usa lol-mode-cine.py, la version en script de esto mismo.
        const string ScriptMutex = "Local\\LolModoCine";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            IntPtr script = Native.OpenMutexW(Native.SYNCHRONIZE, false, ScriptMutex);
            if (script != IntPtr.Zero)
            {
                Native.CloseHandle(script);
                MessageBox.Show(
                    "El script lol-mode-cine.py ya está corriendo y hace lo mismo que esta app.\n\n" +
                    "Cerralo desde su ícono de la bandeja (clic derecho → Salir) y volvé a abrir " + AppName + ".",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool created;
            using (new Mutex(false, InstanceMutex, out created))
            {
                if (!created)
                {
                    // Ya esta abierta (seguramente en la bandeja): que muestre su ventana.
                    Native.PostMessageW(Native.HWND_BROADCAST, MainForm.WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                bool startInTray = args.Contains(Autostart.TrayArg, StringComparer.OrdinalIgnoreCase);
                Application.Run(new MainForm(startInTray));
            }
        }
    }
}

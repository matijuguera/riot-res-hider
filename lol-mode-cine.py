import ctypes
import ctypes.wintypes as wt
import os
import signal
import sys

# Procesos que activan el modo cine (comparacion en minusculas).
# TFT ahora es un cliente aparte hecho en Unreal: TFTClient.exe es el launcher
# y la ventana del juego la crea TFTClient-Win64-Shipping.exe.
GAME_PROCESSES = {
    "league of legends.exe",
    "tftclient.exe",
    "tftclient-win64-shipping.exe",
}

# Tamano exacto del area de juego, por proceso. Vacio = no tocar ninguna ventana.
# TFT solo acepta resoluciones que el monitor declare como modos reales, asi que fijarle
# el tamano a la ventana es la unica forma de tener una medida propia. En modo ventana
# Unreal renderiza al tamano de su ventana, asi que el juego sigue.
GAME_WINDOW_SIZES = {
    "tftclient-win64-shipping.exe": (2370, 1334),
}

# Mientras arranca, el juego puede pisar el tamano un par de veces; se reintenta durante
# este rato y despues no se lo molesta mas, por si lo redimensionas a mano.
RESIZE_GRACE_TICKS = 120  # ~60 s a 500 ms por tick

POLL_INTERVAL_MS = 500
APP_NAME = "LoL modo cine"
ICON_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lol-mode-cine.ico")

SW_HIDE = 0
SW_SHOW = 5
SW_RESTORE = 9

SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010
MONITOR_DEFAULTTONEAREST = 2

WM_DESTROY = 0x0002
WM_CLOSE = 0x0010
WM_QUERYENDSESSION = 0x0011
WM_ENDSESSION = 0x0016
WM_COMMAND = 0x0111
WM_TIMER = 0x0113
WM_LBUTTONUP = 0x0202
WM_RBUTTONUP = 0x0205
WM_TRAY = 0x0400 + 1  # WM_APP + 1

NIM_ADD = 0
NIM_MODIFY = 1
NIM_DELETE = 2
NIF_MESSAGE = 0x01
NIF_ICON = 0x02
NIF_TIP = 0x04

MF_STRING = 0x0000
MF_SEPARATOR = 0x0800
MF_GRAYED = 0x0001
TPM_RIGHTBUTTON = 0x0002
TPM_RETURNCMD = 0x0100

IMAGE_ICON = 1
LR_LOADFROMFILE = 0x0010
IDI_APPLICATION = 32512
SM_CXSMICON = 49
SM_CYSMICON = 50

ID_EXIT = 1
TIMER_ID = 1

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
shell32 = ctypes.windll.shell32

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)


class WNDCLASSW(ctypes.Structure):
    _fields_ = [
        ("style", wt.UINT),
        ("lpfnWndProc", WNDPROC),
        ("cbClsExtra", ctypes.c_int),
        ("cbWndExtra", ctypes.c_int),
        ("hInstance", wt.HINSTANCE),
        ("hIcon", wt.HICON),
        ("hCursor", wt.HANDLE),
        ("hbrBackground", wt.HANDLE),
        ("lpszMenuName", wt.LPCWSTR),
        ("lpszClassName", wt.LPCWSTR),
    ]


class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wt.DWORD),
        ("hWnd", wt.HWND),
        ("uID", wt.UINT),
        ("uFlags", wt.UINT),
        ("uCallbackMessage", wt.UINT),
        ("hIcon", wt.HICON),
        ("szTip", wt.WCHAR * 128),
        ("dwState", wt.DWORD),
        ("dwStateMask", wt.DWORD),
        ("szInfo", wt.WCHAR * 256),
        ("uVersion", wt.UINT),
        ("szInfoTitle", wt.WCHAR * 64),
        ("dwInfoFlags", wt.DWORD),
        ("guidItem", ctypes.c_byte * 16),
        ("hBalloonIcon", wt.HICON),
    ]


class MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wt.DWORD),
        ("rcMonitor", wt.RECT),
        ("rcWork", wt.RECT),
        ("dwFlags", wt.DWORD),
    ]


user32.FindWindowW.restype = wt.HWND
user32.FindWindowExW.restype = wt.HWND
user32.GetForegroundWindow.restype = wt.HWND
user32.MonitorFromWindow.restype = wt.HANDLE
user32.MonitorFromWindow.argtypes = [wt.HWND, wt.DWORD]
user32.DefWindowProcW.restype = LRESULT
user32.DefWindowProcW.argtypes = [wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.CreateWindowExW.restype = wt.HWND
user32.CreateWindowExW.argtypes = [
    wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
    ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wt.HWND, wt.HMENU, wt.HINSTANCE, wt.LPVOID,
]
user32.LoadImageW.restype = wt.HANDLE
user32.LoadIconW.restype = wt.HICON
user32.CreatePopupMenu.restype = wt.HMENU
user32.AppendMenuW.argtypes = [wt.HMENU, wt.UINT, ctypes.c_size_t, wt.LPCWSTR]
user32.TrackPopupMenu.restype = ctypes.c_int
user32.TrackPopupMenu.argtypes = [
    wt.HMENU, wt.UINT, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.HWND, wt.LPVOID,
]
user32.SetTimer.restype = ctypes.c_size_t
user32.SetTimer.argtypes = [wt.HWND, ctypes.c_size_t, wt.UINT, wt.LPVOID]
kernel32.GetModuleHandleW.restype = wt.HMODULE
shell32.Shell_NotifyIconW.argtypes = [wt.DWORD, ctypes.POINTER(NOTIFYICONDATAW)]

# --- Cache de handles (se construye una sola vez al inicio) ---
_icon_hwnd = None
_taskbar_hwnd = None


def _build_cache():
    global _icon_hwnd, _taskbar_hwnd

    hwnd_progman = user32.FindWindowW("Progman", None)
    hdef = user32.FindWindowExW(hwnd_progman, None, "SHELLDLL_DefView", None) if hwnd_progman else None

    if not hdef:
        WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
        found = [0]

        def _enum(hwnd, _):
            h = user32.FindWindowExW(hwnd, None, "SHELLDLL_DefView", None)
            if h:
                found[0] = h
                return False
            return True

        user32.EnumWindows(WNDENUMPROC(_enum), 0)
        hdef = found[0]

    if hdef:
        _icon_hwnd = user32.FindWindowExW(hdef, None, "SysListView32", None)

    # LoL siempre corre en el monitor primario -> Shell_TrayWnd
    _taskbar_hwnd = user32.FindWindowW("Shell_TrayWnd", None)


# --- Poll optimizado: solo consulta el proceso cuando cambia el PID ---
_last_pid = 0
_last_name = ""


def foreground_process(hwnd):
    """Nombre del exe duenio de esa ventana, en minusculas. Cacheado por PID."""
    global _last_pid, _last_name

    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))

    if pid.value == _last_pid:
        return _last_name

    _last_pid = pid.value
    hprocess = kernel32.OpenProcess(0x1000, False, pid.value)
    if not hprocess:
        _last_name = ""
        return _last_name

    buf = ctypes.create_unicode_buffer(260)
    size = wt.DWORD(260)
    kernel32.QueryFullProcessImageNameW(hprocess, 0, buf, ctypes.byref(size))
    kernel32.CloseHandle(hprocess)

    _last_name = os.path.basename(buf.value).lower()
    return _last_name


# --- Transición: solo ShowWindow ---
cine_active = False


def activate_cine():
    if _icon_hwnd:
        user32.ShowWindow(_icon_hwnd, SW_HIDE)
    if _taskbar_hwnd:
        user32.ShowWindow(_taskbar_hwnd, SW_HIDE)


def deactivate_cine():
    if _icon_hwnd:
        user32.ShowWindow(_icon_hwnd, SW_SHOW)
    if _taskbar_hwnd:
        user32.ShowWindow(_taskbar_hwnd, SW_SHOW)


# --- Tamano fijo de la ventana del juego ---
_resize_hwnd = None
_resize_left = 0


def keep_window_size(hwnd, name):
    """Le fija a la ventana en foco el area de juego pedida, si es una de las configuradas."""
    global _resize_hwnd, _resize_left

    size = GAME_WINDOW_SIZES.get(name)
    if not size:
        return

    if hwnd != _resize_hwnd:  # ventana nueva: arranca de nuevo el periodo de gracia
        _resize_hwnd, _resize_left = hwnd, RESIZE_GRACE_TICKS
    if _resize_left <= 0:
        return
    _resize_left -= 1

    client = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(client))
    if (client.right, client.bottom) == size:
        return
    if client.right < 200 or client.bottom < 200:  # todavia arrancando
        return

    if user32.IsZoomed(hwnd):  # maximizada: SetWindowPos no la achica
        user32.ShowWindow(hwnd, SW_RESTORE)
        user32.GetClientRect(hwnd, ctypes.byref(client))

    window = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(window))
    extra_w = (window.right - window.left) - client.right
    extra_h = (window.bottom - window.top) - client.bottom
    total_w, total_h = size[0] + extra_w, size[1] + extra_h

    info = MONITORINFO()
    info.cbSize = ctypes.sizeof(MONITORINFO)
    user32.GetMonitorInfoW(
        user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ctypes.byref(info))
    screen = info.rcMonitor
    x = screen.left + ((screen.right - screen.left) - total_w) // 2
    y = screen.top + ((screen.bottom - screen.top) - total_h) // 2

    user32.SetWindowPos(hwnd, None, x, y, total_w, total_h, SWP_NOZORDER | SWP_NOACTIVATE)
    print("Ventana de %s -> %dx%d" % (name, size[0], size[1]))


def poll():
    global cine_active

    hwnd = user32.GetForegroundWindow()
    name = foreground_process(hwnd) if hwnd else ""
    game = name in GAME_PROCESSES

    if game:
        keep_window_size(hwnd, name)

    if game and not cine_active:
        activate_cine()
        cine_active = True
        update_tray_tip()
    elif not game and cine_active:
        deactivate_cine()
        cine_active = False
        update_tray_tip()


# --- Icono en la bandeja ---
_nid = None
_hicon = None
_wndproc_ref = None
_wm_taskbar_created = 0


def _load_icon():
    size_x = user32.GetSystemMetrics(SM_CXSMICON)
    size_y = user32.GetSystemMetrics(SM_CYSMICON)
    if os.path.exists(ICON_PATH):
        h = user32.LoadImageW(None, ICON_PATH, IMAGE_ICON, size_x, size_y, LR_LOADFROMFILE)
        if h:
            return h
    return user32.LoadIconW(None, ctypes.c_void_p(IDI_APPLICATION))


def _make_nid(hwnd):
    nid = NOTIFYICONDATAW()
    nid.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
    nid.hWnd = hwnd
    nid.uID = 1
    nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP
    nid.uCallbackMessage = WM_TRAY
    nid.hIcon = _hicon
    nid.szTip = APP_NAME
    return nid


def update_tray_tip():
    if not _nid:
        return
    _nid.szTip = "{} - {}".format(APP_NAME, "modo cine ON" if cine_active else "esperando juego")
    shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(_nid))


def show_menu(hwnd):
    menu = user32.CreatePopupMenu()
    estado = "Modo cine: ON" if cine_active else "Modo cine: OFF"
    user32.AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, estado)
    user32.AppendMenuW(menu, MF_SEPARATOR, 0, None)
    user32.AppendMenuW(menu, MF_STRING, ID_EXIT, "Salir")

    pt = wt.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    # Requerido para que el menu se cierre al hacer click afuera.
    user32.SetForegroundWindow(hwnd)
    cmd = user32.TrackPopupMenu(
        menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.x, pt.y, 0, hwnd, None
    )
    user32.PostMessageW(hwnd, 0, 0, 0)
    user32.DestroyMenu(menu)

    if cmd == ID_EXIT:
        user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)


def wndproc(hwnd, msg, wparam, lparam):
    if msg == WM_TRAY:
        if (lparam & 0xFFFF) in (WM_RBUTTONUP, WM_LBUTTONUP):
            show_menu(hwnd)
        return 0

    if msg == WM_COMMAND and (wparam & 0xFFFF) == ID_EXIT:
        user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
        return 0

    if msg == WM_TIMER and wparam == TIMER_ID:
        poll()
        return 0

    if msg == _wm_taskbar_created:
        # Explorer se reinicio: se pierde el icono y los handles cacheados.
        _build_cache()
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(_nid))
        update_tray_tip()
        return 0

    if msg in (WM_QUERYENDSESSION, WM_ENDSESSION):
        if cine_active:
            deactivate_cine()
        return 1 if msg == WM_QUERYENDSESSION else 0

    if msg == WM_CLOSE:
        user32.DestroyWindow(hwnd)
        return 0

    if msg == WM_DESTROY:
        user32.PostQuitMessage(0)
        return 0

    return user32.DefWindowProcW(hwnd, msg, wparam, lparam)


def create_window():
    global _wndproc_ref, _wm_taskbar_created

    _wndproc_ref = WNDPROC(wndproc)
    hinst = kernel32.GetModuleHandleW(None)

    wc = WNDCLASSW()
    wc.lpfnWndProc = _wndproc_ref
    wc.hInstance = hinst
    wc.lpszClassName = "LolModoCineWnd"
    if not user32.RegisterClassW(ctypes.byref(wc)):
        raise ctypes.WinError(ctypes.get_last_error())

    # Ventana oculta (no message-only: necesita recibir el broadcast TaskbarCreated).
    hwnd = user32.CreateWindowExW(
        0, wc.lpszClassName, APP_NAME, 0, 0, 0, 0, 0, None, None, hinst, None
    )
    if not hwnd:
        raise ctypes.WinError(ctypes.get_last_error())

    _wm_taskbar_created = user32.RegisterWindowMessageW("TaskbarCreated")
    return hwnd


def cleanup(signum=None, frame=None):
    sys.exit(0)


signal.signal(signal.SIGINT, cleanup)
signal.signal(signal.SIGTERM, cleanup)

# Sin esto Windows devuelve coordenadas virtualizadas en pantallas escaladas (125%, 150%),
# y el tamano que se le fija a la ventana del juego sale mal por ese factor.
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)  # per-monitor, Win 8.1+
except (AttributeError, OSError):
    user32.SetProcessDPIAware()

_build_cache()
_hwnd = create_window()
_hicon = _load_icon()
_nid = _make_nid(_hwnd)
shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(_nid))
update_tray_tip()
user32.SetTimer(_hwnd, TIMER_ID, POLL_INTERVAL_MS, None)

print("Corriendo. Click derecho en el icono de la bandeja -> Salir.")

try:
    msg = wt.MSG()
    while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
        user32.TranslateMessage(ctypes.byref(msg))
        user32.DispatchMessageW(ctypes.byref(msg))
finally:
    if cine_active:
        deactivate_cine()
    shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(_nid))

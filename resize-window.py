"""Resize a game window to an exact size, ignoring the game's resolution list.

Teamfight Tactics only offers resolutions your monitor reports as real display modes, so a
custom size like 2370x1334 is silently replaced by the closest mode it knows (typically
1920x1200). League of Legends has no such restriction — set-resolution.py is enough there.

In windowed mode Unreal renders at whatever size its window happens to be, so resizing the
window from outside gets the exact size the settings menu refuses to give you. This only moves
and resizes a window through the same Win32 calls the taskbar and Alt+Tab use; it never reads or
writes the game's memory.

Requires the game to be in windowed mode. Run it once the game is open:

    python resize-window.py --game tft --resolution 2370x1334
    python resize-window.py --game tft --inches 24.5 --monitor 27 --watch
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

GAMES = {
    "tft": ["TFTClient-Win64-Shipping.exe", "TFTClient.exe"],
    "lol": ["League of Legends.exe"],
}

SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010
SW_RESTORE = 9

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

user32.GetForegroundWindow.restype = wt.HWND
user32.MonitorFromWindow.restype = wt.HANDLE
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT),
                ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


def fail(msg):
    sys.exit("error: " + msg)


def process_name(pid):
    handle = kernel32.OpenProcess(0x1000, False, pid)
    if not handle:
        return ""
    buf = ctypes.create_unicode_buffer(260)
    size = wt.DWORD(260)
    kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size))
    kernel32.CloseHandle(handle)
    return os.path.basename(buf.value)


def game_windows(names):
    """Ventanas visibles con area de cliente real de esos procesos."""
    wanted = {n.lower() for n in names}
    found = []

    def callback(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        name = process_name(pid.value)
        if name.lower() not in wanted:
            return True
        client = wt.RECT()
        user32.GetClientRect(hwnd, ctypes.byref(client))
        if client.right < 200 or client.bottom < 200:  # ventanas auxiliares
            return True
        found.append((hwnd, name, client.right, client.bottom))
        return True

    user32.EnumWindows(WNDENUMPROC(callback), 0)
    return found


def frame_size(hwnd):
    """Cuanto ocupan bordes y barra de titulo, para pedir el area de juego exacta."""
    window, client = wt.RECT(), wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(window))
    user32.GetClientRect(hwnd, ctypes.byref(client))
    return ((window.right - window.left) - client.right,
            (window.bottom - window.top) - client.bottom)


def monitor_rect(hwnd):
    info = MONITORINFO()
    info.cbSize = ctypes.sizeof(MONITORINFO)
    handle = user32.MonitorFromWindow(hwnd, 2)  # MONITOR_DEFAULTTONEAREST
    user32.GetMonitorInfoW(handle, ctypes.byref(info))
    return info.rcMonitor


def set_client_size(hwnd, width, height, position):
    if user32.IsZoomed(hwnd):  # maximizada: SetWindowPos no la achica
        user32.ShowWindow(hwnd, SW_RESTORE)

    extra_w, extra_h = frame_size(hwnd)
    total_w, total_h = width + extra_w, height + extra_h

    if position == "center":
        screen = monitor_rect(hwnd)
        x = screen.left + ((screen.right - screen.left) - total_w) // 2
        y = screen.top + ((screen.bottom - screen.top) - total_h) // 2
    elif position == "keep":
        rect = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        x, y = rect.left, rect.top
    else:
        x, y = position

    user32.SetWindowPos(hwnd, None, x, y, total_w, total_h, SWP_NOZORDER | SWP_NOACTIVATE)

    client = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(client))
    return client.right, client.bottom


def parse_resolution(text):
    for sep in ("x", "X", "*", ","):
        if sep in text:
            w, _, h = text.partition(sep)
            try:
                return int(w.strip()), int(h.strip())
            except ValueError:
                break
    fail("could not read a resolution from %r (expected something like 2370x1334)" % text)


def parse_position(text):
    if text in ("center", "keep"):
        return text
    return parse_resolution(text)


def desktop_resolution():
    return user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)


def main(argv):
    p = argparse.ArgumentParser(
        description="Resize a game window to an exact size, bypassing the game's resolution list.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="examples:\n"
               "  python resize-window.py --game tft --resolution 2370x1334\n"
               "  python resize-window.py --game tft --inches 24.5 --monitor 27 --watch\n"
               "  python resize-window.py --list\n",
    )
    p.add_argument("--game", choices=sorted(GAMES), default="tft",
                   help="which game to resize (default: tft)")
    p.add_argument("--process", metavar="NAME", help="target this executable instead")
    p.add_argument("--resolution", metavar="WxH", help="exact size of the game area")
    p.add_argument("--inches", type=float, metavar="N", help="virtual monitor size, e.g. 24.5")
    p.add_argument("--monitor", type=float, metavar="N", help="your monitor size, e.g. 27")
    p.add_argument("--position", metavar="POS", default="center",
                   help="center (default), keep, or x,y")
    p.add_argument("--watch", action="store_true",
                   help="keep the size applied until you press Ctrl+C")
    p.add_argument("--interval", type=float, default=2.0, metavar="SEC",
                   help="how often --watch checks (default: 2)")
    p.add_argument("--list", action="store_true", help="show the game windows it can see")
    args = p.parse_args(argv)

    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # per-monitor, Win 8.1+
    except (AttributeError, OSError):
        user32.SetProcessDPIAware()

    names = [args.process] if args.process else GAMES[args.game]

    if args.list:
        for game, procs in GAMES.items():
            for hwnd, name, w, h in game_windows(procs):
                print("%-5s %-32s %dx%d  (hwnd %d)" % (game, name, w, h, hwnd))
        return 0

    native = desktop_resolution()
    if args.resolution:
        width, height = parse_resolution(args.resolution)
    elif args.inches and args.monitor:
        factor = args.inches / args.monitor
        width = int(round(native[0] * factor))
        height = int(round(native[1] * factor))
        width, height = width - (width % 2), height - (height % 2)
    else:
        fail("pass --resolution, or both --inches and --monitor")

    if width > native[0] or height > native[1]:
        fail("%dx%d does not fit on a %dx%d screen" % (width, height, native[0], native[1]))

    position = parse_position(args.position)
    target = "%dx%d" % (width, height)
    print("target game area: %s   (desktop %dx%d)" % (target, native[0], native[1]))

    applied_once = False
    while True:
        windows = game_windows(names)
        if not windows:
            if not args.watch:
                fail("no window found for: %s — is the game open and in windowed mode?"
                     % ", ".join(names))
            print("waiting for %s ..." % names[0])
        for hwnd, name, current_w, current_h in windows:
            if (current_w, current_h) == (width, height):
                if not applied_once:
                    print("%s is already %s" % (name, target))
                    applied_once = True
                continue
            got_w, got_h = set_client_size(hwnd, width, height, position)
            print("%s: %dx%d -> %dx%d" % (name, current_w, current_h, got_w, got_h))
            applied_once = True
            if (got_w, got_h) != (width, height):
                print("    the window did not take the exact size; it may not be in windowed mode")

        if not args.watch:
            return 0
        try:
            time.sleep(args.interval)
        except KeyboardInterrupt:
            print("\nstopped")
            return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        sys.exit(0)

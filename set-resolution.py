"""Turn a big monitor into a smaller one for League of Legends and Teamfight Tactics.

Pros play on 24.5" screens: the whole game fits inside a smaller cone of vision, so you catch
more of the screen without moving your eyes, and the mouse travels less. On a 27" you can get
the same feel by rendering the game at a proportionally smaller resolution and playing in a
window.

The script writes that resolution into each game's own config file:

  League of Legends   <install>\\Config\\game.cfg
  Teamfight Tactics   %LOCALAPPDATA%\\TFT\\Saved\\Config\\WindowsClient\\GameUserSettings.ini

Both games must be closed: they rewrite their config on exit and would undo the change.
"""

import argparse
import ctypes
import os
import shutil
import stat
import subprocess
import sys

LOL_DEFAULT = r"C:\Riot Games\League of Legends"
TFT_DEFAULT = os.path.join(os.environ.get("LOCALAPPDATA", ""), "TFT")

LOL_SECTION = "General"
TFT_SECTION = "/Script/TFTSettings.TFTUserSettings"

# Enteros que cada juego guarda para el modo de ventana.
# TFT es Unreal, donde el enum es 0 fullscreen / 1 windowed fullscreen / 2 windowed.
# El de LoL esta deducido de configs reales, por eso el modo no se toca salvo que lo pidas.
LOL_MODES = {"fullscreen": 0, "borderless": 1, "windowed": 2}
TFT_MODES = {"fullscreen": 0, "borderless": 1, "windowed": 2}

LOL_PROCESSES = ["League of Legends.exe", "LeagueClient.exe", "LeagueClientUx.exe"]
TFT_PROCESSES = ["TFTClient.exe", "TFTClient-Win64-Shipping.exe"]


# --- utilidades ---

def fail(msg):
    sys.exit("error: " + msg)


def desktop_resolution():
    """Resolucion real del monitor primario, en pixeles fisicos."""
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass
    user32 = ctypes.windll.user32
    return user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)


def running_processes(names):
    """Devuelve cuales de esos procesos estan corriendo."""
    try:
        out = subprocess.run(
            ["tasklist", "/fo", "csv", "/nh"],
            capture_output=True, text=True, creationflags=0x08000000,
        ).stdout.lower()
    except (OSError, subprocess.SubprocessError):
        return []
    return [n for n in names if ('"%s"' % n.lower()) in out]


def parse_resolution(text):
    for sep in ("x", "X", "*", ","):
        if sep in text:
            w, _, h = text.partition(sep)
            try:
                return int(w.strip()), int(h.strip())
            except ValueError:
                break
    fail("could not read a resolution from %r (expected something like 2370x1334)" % text)


def scaled_resolution(native, monitor_inches, target_inches):
    """Misma relacion de aspecto, escalada por la diagonal. Redondeada a pares."""
    if monitor_inches <= 0 or target_inches <= 0:
        fail("monitor sizes must be positive")
    if target_inches > monitor_inches:
        fail("target size (%.1f\") is bigger than the monitor (%.1f\")"
             % (target_inches, monitor_inches))
    factor = target_inches / monitor_inches
    w = int(round(native[0] * factor))
    h = int(round(native[1] * factor))
    return w - (w % 2), h - (h % 2)


# --- edicion de archivos ini preservando todo lo demas ---

def is_readonly(path):
    return not os.access(path, os.W_OK)


def read_text(path):
    """Devuelve (texto, encoding, tenia_bom). El BOM se maneja aparte para no agregar uno
    donde no lo habia: game.cfg sin BOM + BOM = archivo que el juego puede no parsear."""
    with open(path, "rb") as fh:
        raw = fh.read()

    had_bom = raw.startswith(b"\xef\xbb\xbf")
    if had_bom:
        raw = raw[3:]

    for enc in ("utf-8", "cp1252"):
        try:
            return raw.decode(enc), enc, had_bom
        except UnicodeDecodeError:
            continue
    fail("could not decode %s" % path)


def set_key(text, section, key, value):
    """Cambia (o agrega) key=value dentro de section. Devuelve (texto, valor_anterior)."""
    lines = text.splitlines(keepends=True)
    target = section.strip().lower()
    key_l = key.lower()
    current = None
    last_line = None

    for i, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            if current == target:
                break
            current = stripped[1:-1].strip().lower()
            if current == target:
                last_line = i  # cabecera de la seccion buscada
            continue
        if current != target:
            continue
        if stripped and not stripped.startswith(";"):
            last_line = i
            name = stripped.split("=", 1)[0].strip().lower()
            if name == key_l:
                old = stripped.split("=", 1)[1].strip() if "=" in stripped else ""
                eol = line[len(line.rstrip("\r\n")):] or "\n"
                if old == str(value):
                    return text, old
                lines[i] = "%s=%s%s" % (key, value, eol)
                return "".join(lines), old

    eol = "\r\n" if "\r\n" in text else "\n"
    entry = "%s=%s%s" % (key, value, eol)
    if last_line is None:  # la seccion no existe
        prefix = "" if (not text or text.endswith("\n")) else eol
        return text + prefix + "[%s]%s%s" % (section, eol, entry), None
    lines.insert(last_line + 1, entry)
    return "".join(lines), None


def apply_changes(path, section, changes, dry_run):
    """changes: lista de (key, value). Devuelve la lista de cambios reales."""
    text, encoding, had_bom = read_text(path)
    applied = []
    for key, value in changes:
        text, old = set_key(text, section, key, value)
        if old != str(value):
            applied.append((key, old, value))

    if not applied or dry_run:
        return applied, is_readonly(path)

    backup = path + ".bak"
    if not os.path.exists(backup):  # guarda el original, no la ultima corrida
        shutil.copy2(path, backup)

    # Mucha gente marca game.cfg como solo-lectura para que el juego no lo pise al salir.
    # Se destilda para escribir y se vuelve a dejar como estaba.
    readonly = is_readonly(path)
    if readonly:
        os.chmod(path, stat.S_IWRITE)
    try:
        with open(path, "wb") as fh:
            if had_bom:
                fh.write(b"\xef\xbb\xbf")
            fh.write(text.encode(encoding))
    finally:
        if readonly:
            os.chmod(path, stat.S_IREAD)
    return applied, readonly


# --- resolucion de rutas ---

def find_lol_config(path):
    if os.path.isfile(path):
        return path
    for candidate in (os.path.join(path, "Config", "game.cfg"), os.path.join(path, "game.cfg")):
        if os.path.isfile(candidate):
            return candidate
    fail("no game.cfg under %s (expected <install folder>\\Config\\game.cfg)" % path)


def find_tft_config(path):
    if os.path.isfile(path):
        return path
    tails = [
        ("Saved", "Config", "WindowsClient", "GameUserSettings.ini"),
        ("Config", "WindowsClient", "GameUserSettings.ini"),
        ("WindowsClient", "GameUserSettings.ini"),
        ("GameUserSettings.ini",),
    ]
    for tail in tails:
        candidate = os.path.join(path, *tail)
        if os.path.isfile(candidate):
            return candidate
    fail("no GameUserSettings.ini under %s" % path)


def ask(prompt, default=""):
    try:
        answer = input("%s%s: " % (prompt, (" [%s]" % default) if default else "")).strip()
    except EOFError:
        answer = ""
    return answer.strip('"') or default


# --- programa ---

def build_parser():
    p = argparse.ArgumentParser(
        description="Set League of Legends and Teamfight Tactics to a smaller resolution, "
                    "so a 27\" monitor plays like a 24.5\" one.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="examples:\n"
               "  python set-resolution.py --lol \"C:\\Riot Games\\League of Legends\" --inches 24.5\n"
               "  python set-resolution.py --lol . --tft . --resolution 2370x1334 --mode windowed\n",
    )
    p.add_argument("--lol", metavar="PATH",
                   help="League of Legends install folder, or the game.cfg itself")
    p.add_argument("--tft", metavar="PATH", nargs="?", const=TFT_DEFAULT,
                   help="optional: TFT config folder, or the GameUserSettings.ini itself")
    p.add_argument("--inches", type=float, metavar="N",
                   help="virtual monitor size to emulate, e.g. 24.5")
    p.add_argument("--monitor", type=float, metavar="N",
                   help="physical size of your monitor in inches, e.g. 27")
    p.add_argument("--native", metavar="WxH",
                   help="native resolution (default: detected from the primary monitor)")
    p.add_argument("--resolution", metavar="WxH",
                   help="exact resolution to write, skipping the inches math")
    p.add_argument("--mode", choices=sorted(LOL_MODES),
                   help="also set the window mode (default: leave each game as it is)")
    p.add_argument("--dry-run", action="store_true", help="show what would change, write nothing")
    p.add_argument("--force", action="store_true", help="write even if the games are running")
    p.add_argument("-y", "--yes", action="store_true", help="do not ask for confirmation")
    return p


def main(argv):
    args = build_parser().parse_args(argv)
    interactive = not args.lol

    native = parse_resolution(args.native) if args.native else desktop_resolution()

    lol_path = args.lol or ask("League of Legends folder", LOL_DEFAULT)
    if not lol_path:
        fail("a League of Legends path is required")
    lol_cfg = find_lol_config(lol_path)

    tft_path = args.tft
    if interactive and tft_path is None:
        tft_path = ask("Teamfight Tactics folder (empty to skip)", TFT_DEFAULT)
    tft_cfg = find_tft_config(tft_path) if tft_path else None

    if args.resolution:
        width, height = parse_resolution(args.resolution)
        how = "requested"
    else:
        monitor = args.monitor
        target = args.inches
        if interactive and monitor is None:
            monitor = float(ask("Your monitor size in inches", "27"))
        if interactive and target is None:
            target = float(ask("Size to emulate in inches", "24.5"))
        if monitor is None or target is None:
            fail("pass --resolution, or both --monitor and --inches")
        width, height = scaled_resolution(native, monitor, target)
        how = "%.1f\" of a %.1f\" monitor (%d%%)" % (target, monitor, round(100 * target / monitor))

    print()
    print("native resolution : %dx%d" % native)
    print("new resolution    : %dx%d   (%s)" % (width, height, how))
    print("window mode       : %s" % (args.mode if args.mode else "unchanged"))
    print("league config     : %s" % lol_cfg)
    print("tft config        : %s" % (tft_cfg or "skipped"))
    print()

    busy = running_processes(LOL_PROCESSES) + (running_processes(TFT_PROCESSES) if tft_cfg else [])
    if busy and not args.force:
        fail("close these first, they rewrite their config on exit: " + ", ".join(busy))

    if not args.yes and not args.dry_run:
        if ask("Apply? [y/N]", "n").lower() not in ("y", "yes"):
            print("nothing written")
            return 0

    targets = [("League of Legends", lol_cfg, LOL_SECTION, [
        ("Width", width),
        ("Height", height),
    ] + ([("WindowMode", LOL_MODES[args.mode])] if args.mode else []))]

    if tft_cfg:
        targets.append(("Teamfight Tactics", tft_cfg, TFT_SECTION, [
            ("ResolutionSizeX", width),
            ("ResolutionSizeY", height),
            ("LastUserConfirmedResolutionSizeX", width),
            ("LastUserConfirmedResolutionSizeY", height),
        ] + ([("FullscreenMode", TFT_MODES[args.mode]),
              ("LastConfirmedFullscreenMode", TFT_MODES[args.mode]),
              ("PreferredFullscreenMode", TFT_MODES[args.mode])] if args.mode else [])))

    for name, path, section, changes in targets:
        applied, readonly = apply_changes(path, section, changes, args.dry_run)
        print("%s:" % name)
        if readonly:
            print("    (file is read-only; the attribute was kept)")
        if not applied:
            print("    already up to date")
            continue
        for key, old, new in applied:
            print("    %-34s %s -> %s" % (key, old if old is not None else "(new)", new))
        if not args.dry_run:
            print("    backup: %s" % (path + ".bak"))

    if args.dry_run:
        print("\ndry run, nothing was written")
        return 0

    if not args.mode and tft_cfg:
        print("\nnote: if TFT is set to borderless it will stretch back to the full desktop and")
        print("      ignore this resolution. Re-run with --mode windowed to make it stick.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

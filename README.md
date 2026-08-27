# Riot Res Hider

**Play League of Legends and Teamfight Tactics on a smaller screen than you own — and keep the
desktop out of the way.**

<p align="center">
  <img src="icon.png" width="96" alt="Riot Res Hider icon">
</p>

Two small Windows scripts, no dependencies beyond Python itself, no installer, no admin rights:

| Script | What it is for |
| --- | --- |
| [`set-resolution.py`](set-resolution.py) | Turns your 27" into a 24.5" for both games, by writing a proportionally smaller resolution into each game's config |
| [`resize-window.py`](resize-window.py) | Forces an exact window size on a running game, for when TFT refuses the resolution you asked for |
| [`lol-mode-cine.py`](lol-mode-cine.py) | Cinema mode: hides the taskbar and desktop icons while a Riot game has focus, restores them when you alt-tab |

They complement each other. Once the game renders smaller than your monitor, there is desktop
around it — and that is exactly what cinema mode hides.

**Requirements:** Windows 10 or 11, and Python 3 (any recent version — tested on 3.14).
Download both from the [latest release](https://github.com/matijuguera/riot-res-hider/releases/latest)
or clone the repo.

---

# 1. A smaller monitor: `set-resolution.py`

Nearly every pro plays on a 24.5" screen. The whole game fits in a narrower cone of vision, so you
take in more of the screen without moving your eyes, and your mouse travels less for the same
in-game distance. On a 27" you get the same feel by rendering the game at a proportionally smaller
resolution and playing in a window.

The maths is just the diagonal ratio: 24.5 / 27 = 0.907, so on a 2560x1440 monitor you want
2322x1306. The script works that out for you and writes it into each game's own config file:

| Game | Config file |
| --- | --- |
| League of Legends | `<install folder>\Config\game.cfg` |
| Teamfight Tactics | `%LOCALAPPDATA%\TFT\Saved\Config\WindowsClient\GameUserSettings.ini` |

```bash
python set-resolution.py --lol "C:\Riot Games\League of Legends" --tft --inches 24.5 --monitor 27
```

Run it with no arguments and it asks for the paths and sizes instead. Pass either game, or both:
`--tft` on its own uses the default location, or give it a path. `--lol` takes the install folder
or the `game.cfg` itself.

Useful flags:

| Flag | |
| --- | --- |
| `--resolution 2370x1334` | write an exact resolution and skip the inches maths |
| `--native 2560x1440` | override the detected native resolution |
| `--mode windowed` | also set the window mode (`windowed`, `borderless`, `fullscreen`) |
| `--dry-run` | print what would change and write nothing |
| `--force` | write even if the games are running |
| `-y` | skip the confirmation prompt |

It is careful with your files:

- **Close both games first.** They rewrite their config on exit and would undo the change; the
  script refuses to run while they are open unless you pass `--force`.
- Keeps a `.bak` of the untouched original next to each file, written only the first time.
- Only rewrites the specific keys. Comments, section order, line endings, encoding and a read-only
  attribute all survive — a lot of people mark `game.cfg` read-only so the client cannot overwrite
  it, and the script puts that flag back after writing.

## TFT only accepts resolutions your monitor reports

League writes down whatever you give it. TFT does not: it validates the resolution against the
display modes your monitor advertises and silently falls back to the closest one it knows. Ask for
2370x1334 on a 2560x1440 screen and you get 1920x1200, because nothing between those two exists in
the list. Two separate things bite here:

- In **borderless**, Unreal stretches the window to the whole desktop and overwrites
  `ResolutionSizeX/Y` on exit. `LastUserConfirmedResolutionSizeX/Y` keeps the value you asked for,
  which is how you tell this is what happened.
- In **windowed**, the mode sticks but the resolution is still snapped to a supported one.

You can create a custom display mode in your GPU control panel, and TFT will then accept it like
any other. Or use the next script, which sidesteps the list entirely.

# 2. The exact size anyway: `resize-window.py`

In windowed mode Unreal renders at whatever size its window happens to be. So instead of asking the
game for a resolution, resize its window from outside and the game follows:

```bash
python resize-window.py --game tft --resolution 2370x1334
```

Run it with the game already open and in windowed mode. It only moves and resizes a window through
the same Win32 calls the taskbar and Alt+Tab use — it never reads or writes the game's memory.

| Flag | |
| --- | --- |
| `--game tft` / `--game lol` | which game to target (default `tft`) |
| `--process NAME.exe` | target any other executable instead |
| `--resolution 2370x1334` | exact size of the game area, borders excluded |
| `--inches 24.5 --monitor 27` | same diagonal maths as `set-resolution.py` |
| `--position center` | `center` (default), `keep`, or `x,y` |
| `--watch` | keep the size applied until you press Ctrl+C |
| `--list` | show the game windows it can see, with their current size |

The size lasts as long as that window does, so run it after each launch — or leave `--watch`
running, which also catches the game resizing itself.

Performance-wise this is free, and usually a small win: fewer pixels is less work for the GPU. In a
window there is no upscaling either, so pixels map 1:1 and the image stays sharp — it is just
physically smaller.

---

# 3. Cinema mode: `lol-mode-cine.py`

If you play in *Borderless* or windowed mode, Windows keeps drawing the taskbar on top of the game
and your desktop icons sit around it. This script hides the taskbar and the desktop icons while LoL
or TFT is the focused window, and brings them back the moment you alt-tab away.

## What it does

- Polls the foreground window twice per second and checks which process owns it.
- If it belongs to League of Legends or Teamfight Tactics, it hides the taskbar
  (`Shell_TrayWnd`) and the desktop icon list (`SysListView32`) with `ShowWindow(SW_HIDE)`.
- When any other window takes focus, everything comes back.
- Lives in the system tray: right-click the icon to see the current state and quit.

Games detected out of the box:

| Game | Process |
| --- | --- |
| League of Legends (in game) | `League of Legends.exe` |
| Teamfight Tactics (standalone client) | `TFTClient.exe`, `TFTClient-Win64-Shipping.exe` |

TFT recently moved to its own Unreal-based client, where the lobby and the match run in the same
process — so cinema mode kicks in as soon as the TFT client is focused, not only during a game.
League still only triggers in game, because `League of Legends.exe` does not exist while you are in
the client.

## Why it is light

The whole thing is `ctypes` against `user32`/`kernel32`/`shell32` — the standard library only.

- Window handles for the taskbar and the desktop icons are resolved **once** at startup and cached.
- The foreground PID is cached, so the process name is only queried when focus actually changes to
  a different process.
- The main loop is a real Win32 message loop driven by `SetTimer`, not a busy `sleep` loop.
- Transitions are a single `ShowWindow` call. Nothing is written to disk, the registry, or the
  network.

It also survives an Explorer restart: the script listens for the `TaskbarCreated` broadcast, then
re-registers its tray icon and rebuilds the cached handles.

## How to run it

Grab `lol-mode-cine.py` and `lol-mode-cine.ico` from the
[latest release](https://github.com/matijuguera/riot-res-hider/releases/latest) (or clone the repo)
and keep both files in the same folder — the script loads the icon from its own directory.

Run it without a console window:

```bash
pythonw lol-mode-cine.py
```

Or run it with a console if you want to see the startup message:

```bash
python lol-mode-cine.py
```

Then right-click the tray icon to check the state or quit. On Windows 11 new tray icons land in the
hidden-icons overflow (the `^` chevron); drag it onto the taskbar, or pin it from
*Settings › Personalization › Taskbar › Other system tray icons*.

### Start it with Windows

Press `Win + R`, run `shell:startup`, and drop a shortcut in that folder with:

| Field | Value |
| --- | --- |
| Target | `C:\Path\To\pythonw.exe "C:\Path\To\lol-mode-cine.py"` |
| Start in | the folder containing the script |
| Icon | `lol-mode-cine.ico` |

Use `pythonw.exe`, not `python.exe`, so no console window shows up at boot.

## Configuration

Everything worth tweaking is at the top of the file:

```python
GAME_PROCESSES = {
    "league of legends.exe",
    "tftclient.exe",
    "tftclient-win64-shipping.exe",
}

POLL_INTERVAL_MS = 500
```

Add any executable name (lowercase) to `GAME_PROCESSES` to use it with other games — VALORANT,
Steam titles, anything.

## Notes and limitations

- Built for **Borderless / windowed** play. In exclusive fullscreen Windows already hides the
  taskbar and this does nothing useful.
- Only the primary monitor's taskbar is hidden (`Shell_TrayWnd`). Secondary taskbars
  (`Shell_SecondaryTrayWnd`) are left alone on purpose, so a second screen keeps working normally.
- The taskbar and icons are always restored on exit, including on shutdown and sign-out.
- The tray menu is in Spanish (`Salir` = quit).

## License

MIT — do whatever you want with it.

# Riot Res Hider

**Cinema mode for League of Legends and Teamfight Tactics.**

If you play in *Borderless* mode, Windows keeps drawing the taskbar on top of the game and your
desktop icons flash every time the game loses focus for a second. This tiny script fixes that: it
hides the taskbar and the desktop icons while LoL or TFT is the focused window, and brings them
back the moment you alt-tab away.

No installer, no dependencies, no admin rights. One Python file and an icon.

<p align="center">
  <img src="icon.png" width="96" alt="Riot Res Hider icon">
</p>

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

## Requirements

- Windows 10 or 11
- Python 3 (any recent version — tested on 3.14)

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

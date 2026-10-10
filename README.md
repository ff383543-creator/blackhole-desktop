# BlackHole: a black hole instead of the Recycle Bin on your desktop

**English** | [Русский](README.ru.md)

Replaces the desktop "Recycle Bin" icon with an animated black hole with gravitational lensing:
it bends the wallpaper, icons and mouse cursor around it, and a disk of gas swirls around the horizon.
A file you drop near the hole is pulled in along a spiral and goes into the **normal Windows Recycle Bin**, so you can always restore it.

Windows 10 (2004+) / Windows 11, x64 only. No installer and no dependencies: it is built with the C# compiler that already ships with Windows.

## Install

1. Download or clone this repository into any folder (for example `C:\Users\<you>\BlackHole`).
2. Run `build.cmd` (double-click). This produces `BlackHole.exe`.
3. Run `BlackHole.exe`. A tray icon appears and the black hole shows up on the desktop where the Recycle Bin used to be.

Start with Windows: right-click the tray icon, then "Запускать вместе с Windows" (Run at Windows startup).

## Usage

| Action | What happens |
|---|---|
| Drag a file or shortcut toward the hole | The hole "gets hungry" and your cursor is slowly tugged toward it; you can pull away |
| Drop a file near the hole (within about 2.4 shadow radii) | The icon spirals in and the file is moved to the Recycle Bin |
| Double-click the hole | Opens the Recycle Bin |
| Move the cursor close to the hole | The real arrow is hidden and a distorted copy is drawn instead, swirling and shrinking into the hole |
| Press **O** on the desktop (or tray menu) | The hole starts slowly pulling **all desktop icons** toward itself (they stop in a ring around it, nothing is deleted). The taskbar is swallowed too: a snapshot of it is torn into pieces that spiral into the hole while the real taskbar is hidden |
| Press **P** on the desktop (or **Ctrl+Alt+P** anywhere) | Stops it, puts every icon back and brings the taskbar back |
| Reboot / quit the program | Icons are put back automatically (also on the next start if the PC was shut down mid-effect) |
| Any open application window | The hole freezes and uses almost no CPU; it comes back to life on a clear desktop |

Tray menu (labels are in Russian): open the Recycle Bin, empty the Recycle Bin (with the Windows confirmation), size (tiny / small / normal), move the hole to the cursor position (after 3 s), run at startup, quit.

## What it changes on your computer

- During the swallow effect it hides the real taskbar (`ShowWindow`, not a setting). It is brought back by key P, on quit, at logoff, on the next start, or with `restore.cmd`.
- Hides the "Recycle Bin" desktop icon (registry `HKCU\...\Explorer\HideDesktopIcons`). Quitting from the tray menu brings the icon back.
- While the cursor is inside the hole's zone, it temporarily replaces the system cursors with blank ones. They are restored when you leave the zone, when the app exits, and on the next start.
- Settings (position, size) are stored in `%APPDATA%\BlackHole\settings.ini`.
- Installs a global mouse hook (needed for the double-click and for detecting drags). It records nothing and sends nothing anywhere.

## If nothing appears

1. The hole only lives on a **clear desktop**: minimize or close all windows (Win+D). With any window open it freezes and is hidden under it.
2. Look for the black-hole icon in the system tray (click the ^ arrow). Right-click it, then **Открыть журнал** (Open log), or open `%APPDATA%\BlackHole\log.txt`. It lists every startup step and any error; send it to the author.
3. `build.cmd` builds and starts the program and stays open so you can read errors. It needs 64-bit Windows 10 (2004+) / 11.

## If something goes wrong

Run `restore.cmd`: it closes the app, brings back the Recycle Bin icon and the normal cursors.
Manually: `BlackHole.exe --quit`, then `BlackHole.exe --restore`.

With the icon hidden you can still open the Recycle Bin from the tray icon or by double-clicking the hole.

## How it works

- `Lens.cs`: Schwarzschild ray tracing, done once at startup (light deflection, accretion disk crossings, photon ring, Doppler beaming).
- `Renderer.cs`: every frame it grabs the screen region, warps it with the precomputed lens table, draws the disk and shows the result in a click-through layered window (`UpdateLayeredWindow`). The window is excluded from screen capture so it does not see itself.
- `ShellDesktop.cs`: reads and moves desktop icons through the COM `IFolderView` interface.
- `Program.cs`: window, tray, mouse hook, the "swallow" logic, and pausing when application windows are open.

## Known limitations

- Primary monitor only. The hole lives on the desktop; application windows are drawn above it.
- Other Windows virtual desktops do not get a hole.
- With "Auto arrange icons" enabled, the swallow animation is skipped (the file simply goes to the Recycle Bin).

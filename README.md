# Mini Player — Windows Taskbar Deskband

A tiny media player that docks **inside the Windows 10 taskbar**. It shows the
current track's title and artist on two scrolling rows, and the whole band acts
as invisible controls — click the **left / middle / right** of the title for
**previous / play-pause / next** — for any app that reports to Windows SMTC
(System Media Transport Controls): YouTube Music in a browser, Spotify, etc.

Scroll the mouse wheel over the band to change system volume.

> **Windows 10 only.** Deskbands (third-party taskbar toolbars) were **removed
> in Windows 11**, so the DLL will not load there. There is no Windows 11 build.

## Features

- Track title + artist on two scrolling rows (scrolls only while hovered; dims while paused;
  collapses to one row on a taskbar too short for two)
- Gesture controls on the title: **left = previous, middle = play/pause, right = next**.
  The band stays plain taskbar text — no buttons, no hover highlight; the About dialog
  shows you the split instead
- Progress bar along the bottom edge — click it to seek within the track
- Mouse wheel over the band changes system volume by 2 units per notch (no OSD banner)
- Middle-click to mute / unmute. Volume and mute both report on the second row and keep
  the track title on the first
- Right-click menu: transport, copy title / artist, language, and an **About / How to use**
  dialog that draws a labeled sample of the player so the gestures are discoverable — shown
  automatically the first time you run it, since nothing on the band advertises the menu
- **English and Vietnamese** (right-click → Language) — applies instantly, remembers your
  choice, and defaults to your Windows display language
- Background samples and matches your taskbar color
- Event-driven via SMTC (no polling)
- Adapts to normal and "Use small taskbar buttons" heights and DPI scaling

## Requirements

- Windows 10
- [.NET SDK](https://dotnet.microsoft.com/download) (builds `net48` via the
  `Microsoft.NETFramework.ReferenceAssemblies` package — no Visual Studio needed)

## Build

```
dotnet build -c Release
```

## Install

Register the COM DLL (writes to the registry, so it self-elevates to admin):

```
scripts\register.bat
```

Then enable it: **right-click the taskbar → Toolbars → Mini Player**.

To remove it: run `scripts\unregister.bat` (admin).

Registration is one-time and survives reboots. Don't move or delete the `bin`
folder — the registration points to `bin\Release\net48\MiniPlayerBand.dll`; if
you move it you must re-register.

## Rebuilding

Explorer loads the DLL in-process and keeps it locked for its whole lifetime, so a
normal `dotnet build` can't overwrite it — and turning the toolbar off doesn't
release it either. Run `scripts\rebuild.ps1`: it compiles, restarts Explorer to
release the lock, then copies the fresh DLL from `obj` to `bin` before Explorer
reloads the band. The CLSID and path are stable, so no re-registration is needed —
just re-enable the toolbar.

## Built with

- [CSDeskBand](https://github.com/ADeltaX/CSDeskBand) — WinForms deskband host (`IDeskBand2` COM plumbing)
- `Microsoft.Windows.SDK.Contracts` — WinRT `Windows.Media.Control` (SMTC) on .NET Framework

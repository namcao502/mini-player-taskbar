# Mini Player — Windows Taskbar Deskband

A tiny media player that docks **inside the Windows 10 taskbar**. It shows the
currently playing track's album art, a scrolling title, and previous /
play-pause / next controls — for any app that reports to Windows SMTC (System
Media Transport Controls): YouTube Music in a browser, Spotify, etc.

Scroll the mouse wheel over the band to change system volume.

> **Windows 10 only.** Deskbands (third-party taskbar toolbars) were **removed
> in Windows 11**, so the DLL will not load there.

## Features

- Album art + scrolling track title + prev / play-pause / next
- Click the album art to toggle play/pause
- Mouse wheel over the band changes system volume by 2 units per notch (no OSD banner)
- Background samples and matches your taskbar color
- Event-driven via SMTC (no polling); title scrolls only while hovered
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
register.bat
```

Then enable it: **right-click the taskbar → Toolbars → Mini Player**.

To remove it: run `unregister.bat` (admin).

Registration is one-time and survives reboots. Don't move or delete the `bin`
folder — the registration points to `bin\Release\net48\MiniPlayerBand.dll`; if
you move it you must re-register.

## Rebuilding

Explorer loads the DLL in-process and keeps it locked while the toolbar is
enabled, so a normal `dotnet build` can't overwrite it. To rebuild: compile,
restart Explorer to release the lock, then copy the fresh DLL from `obj` to
`bin` before Explorer reloads the band. The CLSID and path are stable, so no
re-registration is needed — just re-enable the toolbar.

## Built with

- [CSDeskBand](https://github.com/ADeltaX/CSDeskBand) — WinForms deskband host (`IDeskBand2` COM plumbing)
- `Microsoft.Windows.SDK.Contracts` — WinRT `Windows.Media.Control` (SMTC) on .NET Framework

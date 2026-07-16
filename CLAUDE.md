# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows taskbar **deskband** — a C# `net48` COM shell extension (project at
the repo root; `Band.cs`) that docks *inside* the Windows 10 taskbar and shows the current media
session: album art, scrolling title, and prev / next (clicking the art toggles
play/pause). It reads the
active session via Windows SMTC (System Media Transport Controls) through WinRT,
event-driven (no polling). Works with any app that reports to SMTC (YouTube
Music in a browser, Spotify, etc.). Scrolling the wheel over the band changes
system volume.

**Deprecated tech:** deskbands work on Windows 10 but were **removed in Windows
11** — the DLL won't load there.

## Commands

- Build: `dotnet build -c Release`
- Register (admin): `register.bat` (self-elevates; runs `RegAsm /codebase`)
- Unregister (admin): `unregister.bat`
- Enable: right-click the taskbar > Toolbars > Mini Player

Built with the .NET SDK alone (no Visual Studio); `Microsoft.NETFramework.ReferenceAssemblies`
provides the net48 targeting pack. No tests or linter.

### Rebuild loop (important)

Explorer loads the COM DLL in-process and the CLR keeps it loaded for
Explorer's lifetime, so `bin\Release\net48\MiniPlayerBand.dll` is **locked while
the toolbar is enabled** — `dotnet build`'s copy-to-bin step fails. To rebuild:
compile (obj still builds), then `Stop-Process -Name explorer -Force` and copy
`obj\...\MiniPlayerBand.dll` over `bin\...\MiniPlayerBand.dll` in a short retry
loop that wins the lock before Explorer reloads the band. CLSID and codebase
path are stable, so a rebuild needs **no re-registration** — just re-enable the
toolbar after Explorer restarts.

## Key packages

- `CSDeskBand.Win` — WinForms deskband host (`CSDeskBandWin : UserControl`); does the `IDeskBand2` COM plumbing.
- `Microsoft.Windows.SDK.Contracts` — WinRT `Windows.Media.Control` (SMTC) projections on net48. Awaiting `IAsyncOperation` needs `using System;`.

## Architecture (`Band.cs`)

`Band : CSDeskBandWin` (a WinForms `UserControl`) builds its own children
(`PictureBox` art, `MarqueeLabel` title, two `IconButton`s — prev/next; the art
`Click` toggles play/pause). Non-obvious constraints, most learned the hard way:

- **The base ctor creates the window handle before the derived ctor body runs.**
  So `OnHandleCreated`/`OnLayout` can fire while `_art`/`_title`/buttons are
  still null — both guard against that (and SMTC is started from
  `OnHandleCreated`, not a `HandleCreated += ` subscription, which would miss the
  event). The taskbar color is sampled *first* in the ctor so every child is
  built with the right background.
- **No WinForms `SynchronizationContext` in Explorer**, so `await`s resume off
  the UI thread. Every UI mutation goes through `UiPost` (`BeginInvoke`); SMTC
  work is done first, then marshaled. Forgetting this = silent cross-thread
  failures (blank UI, dead buttons).
- **SMTC is event-driven:** `CurrentSessionChanged` re-hooks the session;
  `MediaPropertiesChanged` → `RefreshAsync`. `RefreshAsync` reloads album art on
  *every* metadata change (art can lag a track otherwise) and is guarded by
  `_refreshSeq` so out-of-order async reads can't apply a stale image.
- **Album art** comes from `props.Thumbnail` read via `DataReader`; failures
  fall back to a solid placeholder.
- **Background matches the taskbar** by sampling the most common pixel color of
  `Shell_TrayWnd` (`TaskbarColor`) and painting the band + children with it.
  True transparency is impossible for a deskband — Explorer paints no taskbar
  background inside the band's rectangle, so "transparent" reveals black. A
  perfect match needs Windows "Transparency effects" off (solid taskbar).
- **Layout is height-adaptive** (`OnLayout`): square art fills the height,
  buttons pin right and are vertically centered, title stretches between — fits
  the normal taskbar, "Use small taskbar buttons", and DPI scaling.
- **Volume on wheel** (`OnWheel`): sets master volume directly via Core Audio
  (`IAudioEndpointVolume.SetMasterVolumeLevelScalar`, ±0.02 = 2 units/notch) —
  chosen over the volume media key so there's **no OSD banner**. The event is
  marked `Handled` to stop it bubbling parent→child (which double-counted the
  step). The new level is shown briefly in the title area, then the track title
  is restored (`SetTitle` suppresses updates while the readout shows).
- **Glyphs** are Segoe MDL2 Assets code points written as `\uXXXX` escapes.
- **`IconButton`** and **`MarqueeLabel`** are owner-drawn: the button centers the
  glyph exactly, and the marquee renders each frame directly to the DC on a
  timer (with a back-buffer) instead of via `Invalidate()`, whose `WM_PAINT` gets
  starved in Explorer's busy message pump; position is time-based (Stopwatch) so
  uneven ticks don't stutter.

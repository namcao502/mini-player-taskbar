# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows taskbar **deskband** — a C# `net48` COM shell extension (project at
the repo root; `Band.cs`) that docks *inside* the Windows 10 taskbar and shows the current media
session: the track title + artist on two scrolling rows, with the title itself
acting as three invisible click zones (prev / play-pause / next). It reads the
active session via Windows SMTC (System Media Transport Controls) through WinRT,
event-driven (no polling). Works with any app that reports to SMTC (YouTube
Music in a browser, Spotify, etc.). Scrolling the wheel over the band changes
system volume.

**Deprecated tech:** deskbands work on Windows 10 but were **removed in Windows
11** — the DLL won't load there. **Windows 10 is the only supported target.** A
standalone floating-window host for Win11 used to live in `app/`; it was removed
when Win11 support was dropped, so `PlayerControl` now has exactly one host.

## Commands

- Build: `dotnet build -c Release`
- Register (admin): `scripts\register.bat` (self-elevates; runs `RegAsm /codebase`)
- Unregister (admin): `scripts\unregister.bat`
- Rebuild + hot-swap while loaded: `scripts\rebuild.ps1`
- Enable: right-click the taskbar > Toolbars > Mini Player
- Test: `dotnet test Tests\MiniPlayerBand.Tests\MiniPlayerBand.Tests.csproj`

Built with the .NET SDK alone (no Visual Studio); `Microsoft.NETFramework.ReferenceAssemblies`
provides the net48 targeting pack. No linter.

`.github/workflows/build-and-test.yml` runs the build and the test project on every push
and PR to `main`, on `windows-latest` (net48 + WinForms + COM interop need a Windows
runner). CI never has the DLL locked the way a dev machine with the toolbar enabled
does, so it doesn't need `rebuild.ps1`'s hot-swap dance -- a plain `dotnet build` works.

### Rebuild loop (important)

Explorer loads the COM DLL in-process and the CLR keeps it loaded for
Explorer's lifetime, so `bin\Release\net48\MiniPlayerBand.dll` is **locked while
the toolbar is enabled** — `dotnet build`'s copy-to-bin step fails. `scripts\rebuild.ps1`
does the whole dance: compile (obj still builds), `Stop-Process -Name explorer -Force`,
then copy `obj\...\MiniPlayerBand.dll` over `bin\...\MiniPlayerBand.dll` in a short retry
loop that wins the lock before Explorer reloads the band. CLSID and codebase
path are stable, so a rebuild needs **no re-registration** — just re-enable the
toolbar after Explorer restarts.

Note the lock survives turning the toolbar *off*: the CLR keeps the DLL loaded for
Explorer's whole lifetime, so only restarting Explorer frees it.

## Key packages

- `CSDeskBand.Win` — WinForms deskband host (`CSDeskBandWin : UserControl`); does the `IDeskBand2` COM plumbing.
- `Microsoft.Windows.SDK.Contracts` — WinRT `Windows.Media.Control` (SMTC) projections on net48. Awaiting `IAsyncOperation` needs `using System;`.

## Layout

Every type is in the flat `MiniPlayerBand` namespace regardless of folder — **do not
introduce folder-shaped namespaces.** RegAsm wrote the literal type name
`MiniPlayerBand.Band` into the registry, so renaming or re-namespacing it makes the
band vanish from the Toolbars menu with no error. `MiniPlayerBand.csproj` stays at the
repo root for the same reason: it fixes the `bin\Release\net48` codebase path RegAsm
registered.

| Path | Holds |
|---|---|
| `Band.cs` | The COM entry point. `[Guid]` + `[CSDeskBandRegistration]` — never edit either. |
| `Player/PlayerControl.cs` | Fields, ctor, `OnHandleCreated`, `SetTitle`, `UiPost`, `Dispose`. |
| `Player/PlayerControl.Theme.cs` | `IsLight`/`Shade`, `ApplyTheme`, `OnUserPreferenceChanged`. |
| `Player/PlayerControl.Paint.cs` | `OnPaintBackground`, `MeasureMetrics`, `DrawProgress`, `RepaintChrome`, `OnLayout`. |
| `Player/PlayerControl.Menu.cs` | Right-click menu, language switch, copy helpers, `ShowAbout`. |
| `Player/PlayerControl.Smtc.cs` | Session pick + re-hook, title/playback/timeline reads, `RunCommand`. The selection/timing/heuristic math is split into `internal static` `*Core`/`Compute*` functions with no `Session` in their signature, specifically so `Tests/` can exercise them -- `Session` is a sealed WinRT COM class and can't be mocked. Keep new logic in the pure function and the instance method a thin wrapper, or the split silently drifts apart. |
| `Player/PlayerControl.Input.cs` | Click zones, the hover tooltip, seek, wheel-volume, mute. |
| `Player/MarqueeLabel.cs` | The scrolling title and the click-zone geometry (`ZoneAt`). Draws nothing on hover, on purpose. |
| `Interop/CoreAudio.cs` | Raw Core Audio COM declarations (vtable order matters). |
| `Interop/SystemVolume.cs` | `Adjust` / `ToggleMute` on the default render endpoint. |
| `Interop/TaskbarColor.cs` | `Sample()` — the taskbar's pixel color. |
| `Interop/ZoneTip.cs` | Native tracking tooltip; the WinForms one cannot show here. |
| `Ui/AboutForm.cs` | About / how-to dialog. Embeds a second, real `PlayerControl` (`firstRunEligible: false`) as a live, interactive demo instead of drawing a mock. |
| `Localization/` | `Lang`, `Strings`, `Loc` (EN/VI table + persistence). |
| `scripts/` | `register.bat`, `unregister.bat`, `rebuild.ps1`. Paths inside resolve `%~dp0..`. |
| `Tests/MiniPlayerBand.Tests/` | xUnit project covering the pure `*Core`/`Compute*` functions above. Nests under the repo root alongside `MiniPlayerBand.csproj`, so that project excludes `Tests\**\*.cs` from its own implicit glob -- otherwise it compiles the test sources (and collides with their generated `AssemblyInfo.cs`) into itself too. |

## Architecture

`Band : CSDeskBandWin` (a WinForms `UserControl`) hosts a single `PlayerControl`
docked fill; all the UI and SMTC logic lives there. `PlayerControl` builds one
child, the `MarqueeLabel` title, which fills the band above a bottom seek strip. There are
no visible buttons: the title is three invisible click zones (left 1/4 prev,
middle half play/pause, right 1/4 next). The title holds two rows: song title on
top, artist below (a `\n` in the string is the row split). Non-obvious
constraints, most learned the hard way:

- **The base ctor creates the window handle before the derived ctor body runs.**
  So `OnHandleCreated`/`OnLayout` can fire while `_title` is
  still null — both guard against that (and SMTC is started from
  `OnHandleCreated`, not a `HandleCreated += ` subscription, which would miss the
  event). The taskbar color is sampled *first* in the ctor so every child is
  built with the right background.
- **No WinForms `SynchronizationContext` in Explorer**, so `await`s resume off
  the UI thread. Every UI mutation goes through `UiPost` (`BeginInvoke`); SMTC
  work is done first, then marshaled. Forgetting this = silent cross-thread
  failures (blank UI, dead buttons).
- **SMTC is event-driven:** `CurrentSessionChanged` re-hooks the session;
  `MediaPropertiesChanged` → `RefreshAsync`. `RefreshAsync` is guarded by
  `_refreshSeq` so out-of-order async reads can't apply stale data. An empty
  title or a transiently-null session while skipping tracks is debounced
  (`_clearTimer`, `ScheduleNoMedia`) so the old track stays shown instead of
  flashing "No media" during the gap.
- **Background matches the taskbar** by sampling the most common pixel color of
  `Shell_TrayWnd` (`TaskbarColor.Sample`) and painting the band + children with it.
  **Text color follows that sample** (`ApplyTheme`): a light taskbar gets dark text,
  a dark one light text — fixed light-on-dark text is invisible in the Windows light
  theme. Hosts repaint their own background from the `ThemeChanged` event + `BandColor`.
  True transparency is impossible for a deskband — Explorer paints no taskbar
  background inside the band's rectangle, so "transparent" reveals black. A
  perfect match needs Windows "Transparency effects" off (solid taskbar).
- **The taskbar sample has three chances to land, and it needs all three.**
  `TaskbarColor.Sample()` returns `Color?`; **null means "could not read the screen",
  never a color.** It used to swallow failures into a black fallback, and a band built
  while the workstation was locked — `CopyFromScreen` throws `handle is invalid` there —
  painted itself `0,0,0` and stayed 16 levels off a real `16,16,16` dark taskbar forever.
  On null, `ApplyTheme` keeps the current colors and calls `RetryTheme`. The three paths:
  - `UserPreferenceChanged` → `RetryTheme` (4 samples over ~2s; the taskbar repaints
    *after* the notification, so one immediate sample reads the old color).
  - `SessionSwitch` (`SessionUnlock` / `ConsoleConnect`) → `RetryTheme`, because unlocking
    is the moment a locked-screen failure starts succeeding.
  - `PollTheme` on every 5th chrome tick — the safety net for whatever the two events
    miss. Without it a wrong color has no way to heal. Keep it.
- **Layout is height-adaptive** (`OnLayout`): the two-row scrolling title fills
  everything above the bottom seek strip. The three pixel metrics (`_barH`,
  `_seekH`, `_titlePad`) are scaled from the title font's line height in
  `MeasureMetrics`, *not* hardcoded — as constants they stay 2/8/2 physical px on
  a 150% taskbar, where the seek target ends up a third of its intended size.
  When a row band is shorter than one line (that is "Use small taskbar buttons":
  ~11px per row against a 15px line), `MarqueeLabel.Measure` joins the two rows
  into `"title - artist"` on one; without that the rows overlap and clip.
- **Every control is an invisible gesture**, which is the central UX risk here.
  **At rest the band must read as plain taskbar text** — measured, both zone columns
  match the background exactly. Two things carry discoverability, and neither is
  redundant:
  - A hover tooltip naming the zone (`ZoneTip` + `ShowZoneTip`, armed by `_tipTimer`
    500ms after the pointer crosses into a zone). It is anchored at that zone's *left
    edge*, so it says which control this is and where the boundary is at once — the two
    jobs the band itself no longer does. Nothing is painted on the band anymore: four
    drawn shapes were tried and dropped, and none is worth retrying — a zone tint plus a
    Segoe MDL2 glyph chip (looked like a floating widget pasted into the taskbar),
    always-on full-height lines (read as crossing out the title), always-on 3px edge stubs
    (too faint to notice), and hover-only 1px full-height lines (still marks *where*, never
    *which*). `ZoneAt` stays the single definition of the split, and `ShowZoneTip`'s anchor x
    must be derived from the same 1/4 and 3/4 fractions. `AboutForm` used to compensate with a
    hand-painted mock band (fixed sample text, always-on dividers) since a static diagram was
    the only way to label an invisible split — it now embeds a second, real `PlayerControl`
    instead, so hovering/clicking the demo triggers this same real tooltip and is the
    explanation. That demo is built with `firstRunEligible: false` — `PlayerControl`'s
    first-run About prompt (`Loc.IsFirstRun()` in `OnHandleCreated`) would otherwise fire on
    the demo too and stack a second About on top of the first during a genuine first run.
  - **The tooltip has to be native (`Interop/ZoneTip.cs`), not WinForms.** Both WinForms
    paths were measured to fail here: `SetToolTip`'s automatic placement drops the tip
    *under the cursor*, i.e. inside the band and clipped by the screen's bottom edge, and
    `ToolTip.Show` — the only way to place one by hand — created and positioned the window
    correctly and then never made it visible, because it refuses whenever the control's
    root window is not the active one (`TopLevelControl` is `null` in a deskband and a
    taskbar band is never active). `ShowAlways` only governs the automatic path.
    `ZoneTip` drives a `tooltips_class32` popup with `TTF_TRACK | TTF_ABSOLUTE`. Two traps
    in it: `TTM_GETBUBBLESIZE` reports a 20px tip as 6px, so the height comes from
    `GetWindowRect` after activation and is cached; and `TTF_ABSOLUTE` anchors the
    *top-left*, so placing the tip clear of the taskbar means subtracting that height.
  - `Loc.IsFirstRun()` + `Loc.MarkSeen()` (marker `%AppData%\MiniPlayer\seen.txt`) show
    the About dialog once, ever. **They are split on purpose.** They used to be one
    call that wrote the marker and returned true, while the dialog only opened 1500ms
    later on `_firstRunTimer` — so an Explorer restart inside that window (every
    `rebuild.ps1` does one) silently spent the only chance the user ever gets. Mark
    *after* `ShowDialog` returns, and never from the right-click menu path.
    The right-click About item is the only way back to that dialog afterwards.
- **Volume on wheel** (`OnWheel`): sets master volume directly via Core Audio
  (`IAudioEndpointVolume.SetMasterVolumeLevelScalar`, ±0.02 = 2 units/notch) —
  chosen over the volume media key so there's **no OSD banner**. The event is
  marked `Handled` to stop it bubbling parent→child (which double-counted the
  step). Volume and mute both report through `ShowReadout`, which puts the status
  on row 2 and keeps the track title on row 1, then restores after `_volTimer`
  (`SetTitle` suppresses updates while the readout shows). Mute otherwise
  silences the machine with no sign that this control did it.
- **The title text is drawn with an explicit `backColor`** (the 6-argument
  `TextRenderer.DrawText`), which makes GDI fill each run's box opaquely. Dropping
  that argument is what the removed hover tint required, since an opaque fill would
  have punched the tint back out — with the tint gone, the opaque form is back.
- **Nothing repaints itself inside Explorer.** A posted `WM_PAINT` is starved by the
  taskbar's message pump, so `OnPaintBackground` never runs in the deskband — and a
  `FillRectangle` straight onto the window DC leaves the alpha byte at 0, which the
  composited taskbar surface renders as good as invisible (a `16,16,16` progress bar
  measured **254** against 255). Anything the band must show goes through an opaque
  `Format24bppRgb` back-buffer blitted to the DC: `MarqueeLabel` for the text,
  `RepaintChrome` for the side margins, seek strip and progress bar. Both symptoms
  look like "the feature is broken" but the values in the code are correct — check
  the drawing path before the logic.
- **`_progressTimer` ticks in every playback state, and that is load-bearing.** It is
  the only *periodic* `RepaintChrome`; `OnLayout` and `ApplyTheme` each fire about once.
  It used to be gated on `_playing && _tlEnd > _tlStart`, which left the uncovered
  bottom strip at the window class's **white** (measured `255,255,255` against the
  taskbar's `238,238,238`) whenever the track was paused or its duration unknown —
  the strip had no way to heal. Do not re-add the gate to "save" a 1 Hz blit.
- **`MarqueeLabel`** is owner-drawn: it
  renders each frame directly to the DC on a
  timer (with a back-buffer) instead of via `Invalidate()`, whose `WM_PAINT` gets
  starved in Explorer's busy message pump; position is time-based (Stopwatch) so
  uneven ticks don't stutter.

# Mini Player (KDE Plasma 6 plasmoid)

The Linux / KDE counterpart of the Windows deskband. It docks inside a Plasma
panel and shows the active media session: album art, a scrolling title + artist,
and prev / play-pause / next. Clicking it opens a popup with a seek bar, full
transport (shuffle / repeat) and a system-volume slider. Scrolling the wheel
over the panel widget changes system volume.

Works with any player that speaks MPRIS2 (YouTube Music in a browser, Spotify,
VLC, mpv, ...), same as the Windows version works with anything on SMTC.

## Why a plasmoid (and not a floating window)

Windows deskbands were removed in Win11; on KDE the equivalent "lives in the
panel" surface is a plasmoid. It also sidesteps Wayland's restriction on apps
self-positioning always-on-top windows, needs no autostart wiring (Plasma starts
it), and installs to a user directory so there is no rpm-ostree layering on an
immutable base like Fedora Kinoite.

## Requirements

- KDE Plasma 6 (Wayland or X11).
- `wpctl` (from WirePlumber) for the volume control - present by default on
  Kinoite and most Plasma 6 distros.
- `plasma-sdk` is optional, only for `plasmoidviewer6` previews.

## How it maps to the Windows code

| Windows | Here |
| --- | --- |
| SMTC session (`GlobalSystemMediaTransportControlsSessionManager`) | `org.kde.plasma.private.mpris` `Mpris2Model` / `currentPlayer` |
| Core Audio master volume | `wpctl set-volume @DEFAULT_AUDIO_SINK@` via the executable data source |
| WinForms `MarqueeLabel` | `contents/ui/MarqueeLabel.qml` |
| Deskband / floating window host | compact (panel) + full (popup) representations |

> Note: `org.kde.plasma.private.mpris` is a private Plasma module (no cross-version
> stability guarantee). It is what the stock media controller and other MPRIS
> plasmoids use, so it is the pragmatic choice; if a future Plasma release changes
> it, `main.qml` is where to adapt.

## Install

```sh
./install.sh
```

or directly:

```sh
kpackagetool6 --type Plasma/Applet --install .     # first time
kpackagetool6 --type Plasma/Applet --upgrade .     # updates
```

Then right-click a panel > **Add Widgets** > search **Mini Player** and drop it
on the panel.

## Develop / preview

Isolated preview (needs `plasma-sdk`):

```sh
plasmoidviewer6 -a .
```

Edit-reload loop once it is on a panel:

```sh
kpackagetool6 --type Plasma/Applet --upgrade .
systemctl --user restart plasma-plasmashell        # Wayland
```

## Configure

Right-click the widget > **Configure Mini Player**: toggle album art, toggle
title scrolling, set the wheel volume step, and set the panel width.

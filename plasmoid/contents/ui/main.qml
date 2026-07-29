// Root of the Mini Player plasmoid: the Linux/KDE Plasma 6 counterpart of the
// Windows deskband. It follows the active MPRIS2 session (the analog of Windows
// SMTC) through the private Mpris2Model, and drives system volume through wpctl
// (WirePlumber) via the executable data source - the analog of the Windows
// Core Audio master-volume path.
//
// The compact representation is the in-panel deskband analog; the full
// representation is the click-to-open popup. Both live in their own files and
// reach back here through the `root` id and its helpers.

import QtQuick
import org.kde.plasma.plasmoid
import org.kde.plasma.private.mpris as Mpris
import org.kde.plasma.plasma5support as P5Support

PlasmoidItem {
    id: root

    // Active MPRIS player (title, artist, art, transport, seek, shuffle/repeat).
    // currentPlayer is null when nothing is playing/registered.
    Mpris.Mpris2Model { id: mprisModel }
    readonly property var currentPlayer: mprisModel.currentPlayer

    // Last-read system volume (0..1), shown by the popup slider. wpctl is the
    // source of truth; we mirror it here after each read.
    property real systemVolume: 0

    // Panel tooltip mirrors the current track so it reads without expanding.
    toolTipMainText: currentPlayer && currentPlayer.track ? currentPlayer.track : i18n("Mini Player")
    toolTipSubText: currentPlayer && currentPlayer.artist ? currentPlayer.artist : i18n("No media playing")

    // ---- System volume via wpctl (WirePlumber) ----
    // The executable engine deduplicates by source string, so identical commands
    // (e.g. the same "2%+" each notch) would collapse. A trailing "# <nonce>"
    // shell comment keeps every invocation unique without changing the command.
    property int _nonce: 0
    P5Support.DataSource {
        id: executable
        engine: "executable"
        connectedSources: []
        onNewData: function (source, data) {
            const stdout = data["stdout"] || "";
            if (source.indexOf("get-volume") !== -1) {
                // Output looks like "Volume: 0.42" (optionally " [MUTED]").
                const m = stdout.match(/Volume:\s*([0-9.]+)/);
                if (m)
                    root.systemVolume = parseFloat(m[1]);
            }
            disconnectSource(source); // one-shot
        }
    }

    function shellRun(cmd) {
        executable.connectSource(cmd + " # " + (root._nonce++));
    }

    // sign > 0 raises, sign <= 0 lowers, by the configured step (percent).
    // -l 1.0 stops WirePlumber from boosting above 100%.
    function changeVolume(sign) {
        const step = plasmoid.configuration.volumeStep;
        shellRun("wpctl set-volume -l 1.0 @DEFAULT_AUDIO_SINK@ " + step + "%" + (sign > 0 ? "+" : "-"));
        readVolume();
    }

    function setVolume(v) {
        shellRun("wpctl set-volume -l 1.0 @DEFAULT_AUDIO_SINK@ " + v.toFixed(2));
    }

    function readVolume() {
        shellRun("wpctl get-volume @DEFAULT_AUDIO_SINK@");
    }

    // MPRIS pushes position updates only on events; poll once a second while
    // playing so the popup seek bar advances smoothly between them.
    Timer {
        interval: 1000
        repeat: true
        running: root.currentPlayer && root.currentPlayer.playbackStatus === Mpris.PlaybackStatus.Playing
        onTriggered: if (root.currentPlayer) root.currentPlayer.updatePosition()
    }

    // MPRIS position/length are microseconds. Format as m:ss.
    function formatTime(us) {
        if (!us || us <= 0)
            return "0:00";
        const total = Math.floor(us / 1000000);
        const m = Math.floor(total / 60);
        const s = total % 60;
        return m + ":" + (s < 10 ? "0" + s : s);
    }

    compactRepresentation: CompactRepresentation {}
    fullRepresentation: FullRepresentation {}
}

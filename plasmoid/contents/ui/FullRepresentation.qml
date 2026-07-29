// Click-to-open popup: large album art, track info, a seek bar, full transport
// (shuffle / prev / play-pause / next / repeat) and a system-volume slider.
// The Windows band had no popup; this is the richer surface the floating Win11
// window (app/) offers.
//
// Volume here is system volume via wpctl, consistent with the wheel gesture:
// read on open and polled once a second while visible so external changes show.

import QtQuick
import QtQuick.Layouts
import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents3
import org.kde.plasma.private.mpris as Mpris

ColumnLayout {
    id: full

    readonly property var player: root.currentPlayer

    Layout.minimumWidth: Kirigami.Units.gridUnit * 16
    Layout.minimumHeight: Kirigami.Units.gridUnit * 18
    spacing: Kirigami.Units.smallSpacing

    // Keep the system-volume readout fresh while the popup is open.
    Component.onCompleted: root.readVolume()
    Timer {
        interval: 1000
        repeat: true
        running: full.visible
        onTriggered: root.readVolume()
    }

    // Album art
    Image {
        Layout.alignment: Qt.AlignHCenter
        Layout.preferredWidth: Kirigami.Units.gridUnit * 12
        Layout.preferredHeight: Kirigami.Units.gridUnit * 12
        fillMode: Image.PreserveAspectFit
        source: full.player && full.player.artUrl ? full.player.artUrl : ""
        Kirigami.Icon {
            anchors.fill: parent
            source: "media-optical-audio"
            visible: parent.status !== Image.Ready
        }
    }

    // Track info
    PlasmaComponents3.Label {
        Layout.fillWidth: true
        horizontalAlignment: Text.AlignHCenter
        elide: Text.ElideRight
        font.bold: true
        font.pointSize: Kirigami.Theme.defaultFont.pointSize + 1
        text: full.player && full.player.track ? full.player.track : i18n("No media playing")
    }
    PlasmaComponents3.Label {
        Layout.fillWidth: true
        horizontalAlignment: Text.AlignHCenter
        elide: Text.ElideRight
        opacity: 0.7
        text: full.player && full.player.artist ? full.player.artist : ""
        visible: text !== ""
    }

    // Seek bar with time labels
    ColumnLayout {
        Layout.fillWidth: true
        spacing: 0

        PlasmaComponents3.Slider {
            id: seekSlider
            Layout.fillWidth: true
            from: 0
            to: full.player && full.player.length > 0 ? full.player.length : 1
            value: pressed ? value : (full.player ? full.player.position : 0)
            enabled: full.player ? full.player.canSeek : false
            onMoved: if (full.player) full.player.position = value
        }

        RowLayout {
            Layout.fillWidth: true
            PlasmaComponents3.Label {
                text: root.formatTime(full.player ? full.player.position : 0)
                font.pointSize: Kirigami.Theme.smallFont.pointSize
            }
            Item { Layout.fillWidth: true }
            PlasmaComponents3.Label {
                text: root.formatTime(full.player ? full.player.length : 0)
                font.pointSize: Kirigami.Theme.smallFont.pointSize
            }
        }
    }

    // Transport: shuffle / prev / play-pause / next / repeat
    RowLayout {
        Layout.alignment: Qt.AlignHCenter
        spacing: Kirigami.Units.smallSpacing

        PlasmaComponents3.ToolButton {
            icon.name: "media-playlist-shuffle"
            enabled: full.player !== null
            checked: full.player && full.player.shuffle === Mpris.ShuffleStatus.On
            onClicked: if (full.player) full.player.shuffle = (full.player.shuffle === Mpris.ShuffleStatus.On ? Mpris.ShuffleStatus.Off : Mpris.ShuffleStatus.On)
        }
        PlasmaComponents3.ToolButton {
            icon.name: "media-skip-backward"
            enabled: full.player ? full.player.canGoPrevious : false
            onClicked: if (full.player) full.player.Previous()
        }
        PlasmaComponents3.ToolButton {
            implicitWidth: Kirigami.Units.gridUnit * 3
            implicitHeight: Kirigami.Units.gridUnit * 3
            icon.width: Kirigami.Units.iconSizes.medium
            icon.height: Kirigami.Units.iconSizes.medium
            icon.name: full.player && full.player.playbackStatus === Mpris.PlaybackStatus.Playing ? "media-playback-pause" : "media-playback-start"
            enabled: full.player !== null
            onClicked: if (full.player) full.player.PlayPause()
        }
        PlasmaComponents3.ToolButton {
            icon.name: "media-skip-forward"
            enabled: full.player ? full.player.canGoNext : false
            onClicked: if (full.player) full.player.Next()
        }
        PlasmaComponents3.ToolButton {
            icon.name: full.player && full.player.loopStatus === Mpris.LoopStatus.Track ? "media-playlist-repeat-song" : "media-playlist-repeat"
            enabled: full.player !== null
            checked: full.player && full.player.loopStatus !== Mpris.LoopStatus.None
            onClicked: {
                if (!full.player)
                    return;
                let next = Mpris.LoopStatus.None;
                if (full.player.loopStatus === Mpris.LoopStatus.None)
                    next = Mpris.LoopStatus.Track;
                else if (full.player.loopStatus === Mpris.LoopStatus.Track)
                    next = Mpris.LoopStatus.Playlist;
                full.player.loopStatus = next;
            }
        }
    }

    // System volume (wpctl)
    RowLayout {
        Layout.fillWidth: true
        Kirigami.Icon {
            source: "audio-volume-high"
            Layout.preferredWidth: Kirigami.Units.iconSizes.small
            Layout.preferredHeight: Kirigami.Units.iconSizes.small
        }
        PlasmaComponents3.Slider {
            id: volumeSlider
            Layout.fillWidth: true
            from: 0
            to: 1
            value: pressed ? value : root.systemVolume
            onMoved: root.setVolume(value)
        }
    }

    Item { Layout.fillHeight: true }
}

// In-panel view: the deskband analog. Optional album art, a two-row scrolling
// title/artist, and prev / play-pause / next. Wheel over the art/text area
// changes system volume (like the Windows band); clicking it opens the popup.
//
// Unlike the Windows band, where clicking the title toggled play/pause, the
// play-pause button is always visible here, so the title click opens the richer
// popup instead - the native Plasma behavior.

import QtQuick
import QtQuick.Layouts
import org.kde.plasma.plasmoid
import org.kde.kirigami as Kirigami
import org.kde.plasma.components as PlasmaComponents3
import org.kde.plasma.private.mpris as Mpris

RowLayout {
    id: compactRoot

    readonly property var player: root.currentPlayer

    Layout.preferredWidth: plasmoid.configuration.compactWidth
    spacing: Kirigami.Units.smallSpacing

    // Art + text: click to expand, wheel to change system volume.
    Item {
        Layout.fillWidth: true
        Layout.fillHeight: true

        RowLayout {
            anchors.fill: parent
            spacing: Kirigami.Units.smallSpacing

            Image {
                Layout.fillHeight: true
                Layout.preferredWidth: height
                fillMode: Image.PreserveAspectFit
                source: compactRoot.player && compactRoot.player.artUrl ? compactRoot.player.artUrl : ""
                visible: plasmoid.configuration.showAlbumArt && status === Image.Ready
            }

            ColumnLayout {
                Layout.fillWidth: true
                spacing: 0

                MarqueeLabel {
                    Layout.fillWidth: true
                    Layout.preferredWidth: 0
                    Layout.fillHeight: true
                    scrolling: plasmoid.configuration.scrollTitle
                    font.bold: true
                    font.pixelSize: Kirigami.Units.gridUnit * 0.75
                    text: compactRoot.player && compactRoot.player.track ? compactRoot.player.track : i18n("No media")
                }

                MarqueeLabel {
                    Layout.fillWidth: true
                    Layout.preferredWidth: 0
                    Layout.fillHeight: true
                    scrolling: plasmoid.configuration.scrollTitle
                    opacity: 0.7
                    font.pixelSize: Kirigami.Units.gridUnit * 0.65
                    text: compactRoot.player && compactRoot.player.artist ? compactRoot.player.artist : ""
                    visible: text !== ""
                }
            }
        }

        MouseArea {
            anchors.fill: parent
            acceptedButtons: Qt.LeftButton
            cursorShape: Qt.PointingHandCursor
            onClicked: root.expanded = !root.expanded
            onWheel: wheel => {
                root.changeVolume(wheel.angleDelta.y > 0 ? 1 : -1);
                wheel.accepted = true;
            }
        }
    }

    // Transport controls, always visible so the panel widget is usable without
    // opening the popup.
    RowLayout {
        spacing: 0

        PlasmaComponents3.ToolButton {
            Layout.fillHeight: true
            icon.name: "media-skip-backward"
            enabled: compactRoot.player ? compactRoot.player.canGoPrevious : false
            onClicked: if (compactRoot.player) compactRoot.player.Previous()
        }

        PlasmaComponents3.ToolButton {
            Layout.fillHeight: true
            icon.name: compactRoot.player && compactRoot.player.playbackStatus === Mpris.PlaybackStatus.Playing ? "media-playback-pause" : "media-playback-start"
            enabled: compactRoot.player !== null
            onClicked: if (compactRoot.player) compactRoot.player.PlayPause()
        }

        PlasmaComponents3.ToolButton {
            Layout.fillHeight: true
            icon.name: "media-skip-forward"
            enabled: compactRoot.player ? compactRoot.player.canGoNext : false
            onClicked: if (compactRoot.player) compactRoot.player.Next()
        }
    }
}

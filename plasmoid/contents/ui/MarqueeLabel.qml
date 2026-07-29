// Single-line label that scrolls its text when it does not fit, echoing the
// scrolling title of the Windows band. Unlike the WinForms MarqueeLabel (which
// renders each frame to the DC because Explorer starves WM_PAINT), QML runs a
// normal frame-driven animation, so no manual back-buffer is needed.
//
// The parent sets the width (via a Layout); this clips and scrolls within it.
// When `scrolling` is false, or the text already fits, it elides instead.

import QtQuick
import org.kde.kirigami as Kirigami

Item {
    id: marquee

    property alias text: label.text
    property alias font: label.font
    property alias color: label.color
    property bool scrolling: true
    property int pixelsPerSecond: 30
    property int pauseMs: 1500

    clip: true
    implicitWidth: label.implicitWidth
    implicitHeight: label.implicitHeight

    readonly property bool overflowing: label.implicitWidth > width
    readonly property bool active: scrolling && overflowing

    Text {
        id: label
        y: 0
        height: marquee.height
        verticalAlignment: Text.AlignVCenter
        renderType: Text.NativeRendering
        // When scrolling, take natural width and show everything; otherwise fit
        // the box and elide.
        width: marquee.active ? implicitWidth : marquee.width
        elide: marquee.active ? Text.ElideNone : Text.ElideRight
    }

    // Pause, scroll to the end, pause, snap back, repeat.
    SequentialAnimation {
        id: anim
        running: marquee.active && marquee.visible
        loops: Animation.Infinite
        PropertyAction { target: label; property: "x"; value: 0 }
        PauseAnimation { duration: marquee.pauseMs }
        NumberAnimation {
            target: label
            property: "x"
            from: 0
            to: -Math.max(0, label.implicitWidth - marquee.width)
            duration: Math.max(1, (label.implicitWidth - marquee.width) / marquee.pixelsPerSecond * 1000)
            easing.type: Easing.Linear
        }
        PauseAnimation { duration: marquee.pauseMs }
    }

    // Restart cleanly whenever the track changes.
    onTextChanged: {
        anim.stop();
        label.x = 0;
        if (marquee.active)
            anim.restart();
    }
}

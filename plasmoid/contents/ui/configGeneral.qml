import QtQuick
import QtQuick.Controls as QQC2
import QtQuick.Layouts
import org.kde.kirigami as Kirigami

Kirigami.FormLayout {
    property alias cfg_showAlbumArt: showAlbumArt.checked
    property alias cfg_scrollTitle: scrollTitle.checked
    property alias cfg_volumeStep: volumeStep.value
    property alias cfg_compactWidth: compactWidth.value

    QQC2.CheckBox {
        id: showAlbumArt
        Kirigami.FormData.label: i18n("Album art:")
        text: i18n("Show in panel")
    }

    QQC2.CheckBox {
        id: scrollTitle
        Kirigami.FormData.label: i18n("Title:")
        text: i18n("Scroll when it does not fit")
    }

    QQC2.SpinBox {
        id: volumeStep
        Kirigami.FormData.label: i18n("Volume step (%):")
        from: 1
        to: 25
    }

    QQC2.SpinBox {
        id: compactWidth
        Kirigami.FormData.label: i18n("Panel width (px):")
        from: 80
        to: 600
        stepSize: 10
    }
}

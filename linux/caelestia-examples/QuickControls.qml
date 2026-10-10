// Purpose: a sketch of PC Manager's quick panel as a native Quickshell popup, to show what "native Caelestia
//          modules" would mean compared with the web panel. Brightness, performance profile and battery.
// Dependencies: Quickshell with its UPower service; brightnessctl. Plain Quickshell, no Caelestia internals:
//          inside Caelestia the colours and fonts below would come from its own theme objects.
// Run: qs -p linux/caelestia-examples/QuickControls.qml
// Status: an illustration written without running it. Expect small corrections on first run.
import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import Quickshell
import Quickshell.Io
import Quickshell.Services.UPower

PanelWindow {
    id: panel
    anchors { bottom: true; right: true }
    margins { bottom: 12; right: 12 }
    implicitWidth: 360
    implicitHeight: content.implicitHeight + 32
    color: "transparent"

    readonly property color surface: "#1b1d22"
    readonly property color card: "#262930"
    readonly property color accent: "#ff6a4d"
    readonly property color text: "#e9eaee"
    readonly property color muted: "#9aa0ab"

    Process { id: brightness; command: ["brightnessctl", "set", Math.round(slider.value) + "%"] }

    Rectangle {
        anchors.fill: parent
        radius: 22
        color: panel.surface

        ColumnLayout {
            id: content
            anchors { fill: parent; margins: 16 }
            spacing: 14

            RowLayout {
                Text { text: "Quick controls"; color: panel.text; font.pixelSize: 17; font.weight: Font.DemiBold; Layout.fillWidth: true }
                Text {
                    // 78% · AC, as in the web panel
                    text: Math.round(UPower.displayDevice.percentage * 100) + "% · " + (UPower.onBattery ? "Battery" : "AC")
                    color: panel.muted; font.pixelSize: 13
                }
            }

            ColumnLayout {
                spacing: 6
                Text { text: "Brightness"; color: panel.muted; font.pixelSize: 12 }
                Slider { id: slider; from: 1; to: 100; value: 50; Layout.fillWidth: true; onMoved: brightness.running = true }
            }

            ColumnLayout {
                spacing: 6
                Text { text: "Performance"; color: panel.muted; font.pixelSize: 12 }
                RowLayout {
                    spacing: 8
                    Repeater {
                        model: [
                            { label: "Eco", profile: PowerProfile.PowerSaver },
                            { label: "Smart", profile: PowerProfile.Balanced },
                            { label: "Turbo", profile: PowerProfile.Performance }
                        ]
                        delegate: Rectangle {
                            required property var modelData
                            readonly property bool active: PowerProfiles.profile === modelData.profile
                            Layout.fillWidth: true
                            implicitHeight: 44
                            radius: 14
                            color: active ? panel.accent : panel.card
                            Text { anchors.centerIn: parent; text: parent.modelData.label; color: parent.active ? "#1b1d22" : panel.text; font.pixelSize: 14 }
                            MouseArea { anchors.fill: parent; onClicked: PowerProfiles.profile = parent.modelData.profile }
                        }
                    }
                }
            }
        }
    }
}

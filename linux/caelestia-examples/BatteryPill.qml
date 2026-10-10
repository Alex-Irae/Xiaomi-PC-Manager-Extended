// Purpose: a sketch of a bar item for a Caelestia-style bar: battery level, the charge limit when one is set, and
//          a click that opens PC Manager's quick panel. Shows how the web apps and native pieces can coexist:
//          the pill is native, the panel it opens is still the web one.
// Dependencies: Quickshell with its UPower service; revamp-pc-manager from linux/install.sh.
// Run: qs -p linux/caelestia-examples/BatteryPill.qml   (stand-alone it draws a small bar at the top right)
// Status: an illustration written without running it.
import QtQuick
import Quickshell
import Quickshell.Io
import Quickshell.Services.UPower

PanelWindow {
    anchors { top: true; right: true }
    margins { top: 8; right: 8 }
    implicitWidth: pill.implicitWidth
    implicitHeight: 32
    color: "transparent"

    // The kernel's charge limit, when this laptop offers one (see linux/probe).
    FileView { id: limit; path: "/sys/class/power_supply/BAT0/charge_control_end_threshold" }
    Process { id: open; command: ["revamp-pc-manager", "--toggle"] }

    Rectangle {
        id: pill
        anchors.fill: parent
        implicitWidth: label.implicitWidth + 28
        radius: height / 2
        color: "#262930"

        Text {
            id: label
            anchors.centerIn: parent
            color: "#e9eaee"
            font.pixelSize: 13
            text: {
                const percent = Math.round(UPower.displayDevice.percentage * 100) + "%";
                const held = parseInt(limit.text());
                // "76% · held at 70%": what the Windows tray icon cannot show
                return !UPower.onBattery && held < 100 && Math.round(UPower.displayDevice.percentage * 100) >= held ? percent + " · held at " + held + "%" : percent;
            }
        }
        MouseArea { anchors.fill: parent; onClicked: open.running = true }
    }
}

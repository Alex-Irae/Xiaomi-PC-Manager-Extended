"""Check of PC Manager's Linux backend against a made-up /sys and /proc tree and recorded hyprctl answers.

Purpose: the backend cannot be run against real hardware from Windows; this checks its logic anywhere.
Dependencies: Python 3.11+ only. Outputs: prints PASS, or raises on the first wrong value.
Command: python -B linux/pc-manager/tests/check_backend.py
"""
import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from backend import Hardware, Preferences, Router, System, Unavailable  # noqa: E402

MONITOR = {"name": "eDP-1", "width": 2880, "height": 1800, "refreshRate": 120.0, "x": 0, "y": 0, "scale": 2.0,
           "availableModes": ["2880x1800@120.00Hz", "2880x1800@60.00Hz", "1920x1200@60.00Hz"]}


def tree(root, files):
    for name, text in files.items():
        target = Path(root) / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(str(text), encoding="utf-8")


def demo():
    with tempfile.TemporaryDirectory() as root:
        tree(root, {
            "sys/class/power_supply/BAT0/type": "Battery", "sys/class/power_supply/BAT0/present": 1, "sys/class/power_supply/BAT0/status": "Discharging",
            "sys/class/power_supply/BAT0/capacity": 76, "sys/class/power_supply/BAT0/energy_full": 68775000, "sys/class/power_supply/BAT0/energy_full_design": 72000000,
            "sys/class/power_supply/BAT0/cycle_count": 41, "sys/class/power_supply/BAT0/power_now": 8500000, "sys/class/power_supply/BAT0/charge_control_end_threshold": 80,
            "sys/class/power_supply/ADP1/type": "Mains", "sys/class/power_supply/ADP1/online": 0,
            "sys/class/power_supply/ucsi-source-psy-1/type": "USB", "sys/class/power_supply/ucsi-source-psy-1/online": 0,
            "sys/class/power_supply/ucsi-source-psy-1/voltage_now": 20000000, "sys/class/power_supply/ucsi-source-psy-1/current_max": 5000000,
            "sys/class/backlight/intel_backlight/brightness": 200, "sys/class/backlight/intel_backlight/max_brightness": 400,
            "sys/firmware/acpi/platform_profile": "balanced", "sys/firmware/acpi/platform_profile_choices": "low-power balanced performance",
            "sys/class/dmi/id/sys_vendor": "TIMI", "sys/class/dmi/id/product_name": "Xiaomi Book Pro 14 2026", "sys/class/dmi/id/bios_version": "X1", "sys/class/dmi/id/bios_date": "06/17/2026",
            "sys/class/thermal/thermal_zone0/temp": 47000, "proc/stat": "cpu  100 0 100 800 0 0 0 0 0 0", "proc/meminfo": "MemTotal:       33000000 kB\nMemAvailable:   16500000 kB",
        })
        calls = []

        def run(arguments):
            calls.append(arguments)
            if arguments[:3] == ["hyprctl", "-j", "monitors"]:
                return json.dumps([MONITOR])
            if arguments[:3] == ["hyprctl", "-j", "devices"]:
                return json.dumps({"mice": [{"name": "elan-touchpad"}, {"name": "usb-mouse"}], "touch": [{"name": "goodix-touch"}]})
            if arguments[:2] == ["hyprctl", "keyword"]:
                if arguments[2] == "monitor":
                    MONITOR["refreshRate"] = float(arguments[3].split("@")[1].split(",")[0])
                return "ok"
            raise AssertionError(arguments)

        system = System(root, run=run, which=lambda program: program == "hyprctl")
        hardware = Hardware(system, Preferences(Path(root) / "settings.json"))
        router = Router(hardware)

        state = router.handle("state.read", {})["hardware"]
        assert state["powerSource"] == "Offline" and state["batteryPercent"] == 76 and state["chargeLimit"] == 80, state
        assert state["brightness"] == 50 and state["refreshRate"] == 120 and state["refreshRates"] == [60, 120], state
        assert state["mode"] == "Auto" and state["visibleModes"] == ["Eco", "Auto", "Turbo"], state
        assert state["batteryHealth"] == 96 and state["cycles"] == 41 and round(state["fullWh"], 2) == 68.78, state
        assert state["model"] == "TIMI Xiaomi Book Pro 14 2026" and state["adapterWatts"] is None and state["touchpadEnabled"] is True, state

        # Every field the Windows pages read from the state must be present, even when its value is unknown.
        for name in "adapterWatts batteryHealth batteryPercent bios brightness chargeConflict chargeLimit cycles designWh deviceName fullWh hapticsAvailable mode model powerSource pressure refreshRate refreshRates requestedChargeLimit specs touchpadEnabled touchscreenEnabled travel vibration visibleModes firmwareError firmwareAvailable modeConflict testMode preferences customization".split():
            assert name in state, name
        for name in "acMode acRefreshRate appearance autoRefresh batteryMode batteryRefreshRate compactPanel customAssets developerMode minimizeToTray originalPopupEnabled osdStyle owlFeature popupAnimations profileImage refreshFeature themeAccent themeBackground themePalettes themePreset themeSurface".split():
            assert name in state["preferences"], name

        telemetry = hardware.telemetry()
        assert telemetry["batteryWatts"] == -8.5 and telemetry["batteryPowerState"] == 2 and round(telemetry["memoryPercent"]) == 50, telemetry

        router.handle("performance.set", {"value": "Turbo", "source": "Offline"})
        assert system.read("sys/firmware/acpi/platform_profile") == "performance"
        router.handle("battery.limit", {"value": 70})
        assert system.number("sys/class/power_supply/BAT0/charge_control_end_threshold") == 70 and hardware.preferences["careLimit"] == 70
        router.handle("battery.travel", {"on": True})
        assert system.number("sys/class/power_supply/BAT0/charge_control_end_threshold") == 100 and hardware.read()["requestedChargeLimit"] == 100
        assert hardware.apply_policies()[0].startswith("Travel charging ended")  # on battery: the care limit returns
        assert system.number("sys/class/power_supply/BAT0/charge_control_end_threshold") == 70

        router.handle("display.refresh", {"value": 60})
        assert hardware.refresh()[0] == 60 and ["hyprctl", "keyword", "monitor", "eDP-1,2880x1800@60,0x0,2.0"] in calls
        before = len(calls)
        router.handle("display.refresh", {"value": 60})  # already there: nothing is applied
        assert not any(call[:3] == ["hyprctl", "keyword", "monitor"] for call in calls[before:])
        router.handle("display.automatic", {"on": True, "ac": 120, "battery": 60})

        # The charger is plugged in: mains power, 100 W charger, the automatic rate follows.
        tree(root, {"sys/class/power_supply/BAT0/status": "Charging", "sys/class/power_supply/ADP1/online": 1, "sys/class/power_supply/ucsi-source-psy-1/online": 1})
        assert hardware.apply_policies() == ["Refresh rate 120 Hz"] and hardware.refresh()[0] == 120
        state = hardware.read()
        assert state["powerSource"] == "Online" and state["adapterWatts"] == 100 and state["visibleModes"] == ["Auto", "FullSpeed", "Eco"], state
        assert hardware.telemetry()["batteryPowerState"] == 5

        router.handle("input.enabled", {"device": "touchpad", "on": False})
        assert ["hyprctl", "keyword", "device[elan-touchpad]:enabled", "false"] in calls and hardware.read()["touchpadEnabled"] is False

        for method, args, part in (("display.refresh", {"value": 90}, "not offered"), ("performance.set", {"value": "Quiet"}, "not offered"), ("touchpad.vibration", {"value": "Medium"}, "Not available on Linux")):
            try:
                router.handle(method, args)
            except Unavailable as error:
                assert part in str(error), error
            else:
                raise AssertionError(method)

        # A laptop whose kernel offers neither profiles nor a charge limit says so and keeps the display controls.
        (Path(root) / "sys/firmware/acpi/platform_profile").unlink()
        (Path(root) / "sys/class/power_supply/BAT0/charge_control_end_threshold").unlink()
        state = hardware.read()
        assert state["firmwareAvailable"] is False and "no performance profile" in state["firmwareError"] and state["brightness"] == 50, state
    print("PASS: PC Manager Linux backend")


if __name__ == "__main__":
    demo()

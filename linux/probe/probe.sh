#!/usr/bin/env bash
# Purpose: report what this laptop and this desktop session offer to the Linux apps, so PC Manager's features can
#          be mapped to real kernel interfaces instead of guesses: battery and charge limit, performance profiles,
#          backlight, keyboard backlight, Xiaomi's firmware interface (WMI), sensors, display modes, input
#          devices, graphics and neural-processor drivers, and the programs the apps call.
# Dependencies: bash and coreutils. Reads only; no root needed. Nothing is changed and nothing is sent anywhere.
# Outputs: linux/probe/results/NNN_<utc>_probe/report.txt (a new folder each run) and the same text on screen.
#          It contains hardware names, driver and kernel versions; no personal files, no serial numbers.
# Command: linux/probe/probe.sh
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$here/results"
last="$(find "$here/results" -maxdepth 1 -type d -name '[0-9][0-9][0-9]_*' -printf '%f\n' 2>/dev/null | sort | tail -1 | cut -c1-3)"
folder="$here/results/$(printf '%03d' $((10#${last:-0} + 1)))_$(date -u +%Y%m%dT%H%M%SZ)_probe"
mkdir "$folder"
report="$folder/report.txt"

section() { printf '\n== %s\n' "$1"; }
show() { for path in "$@"; do [ -r "$path" ] && printf '%s: %s\n' "$path" "$(tr '\n' ' ' < "$path" 2>/dev/null | cut -c1-300)"; done; }
have() { if command -v "$1" >/dev/null 2>&1; then printf '%s: %s\n' "$1" "$(command -v "$1")"; else printf '%s: MISSING\n' "$1"; fi; }

{
  section "System"
  uname -srm
  show /etc/os-release | head -1
  show /sys/class/dmi/id/sys_vendor /sys/class/dmi/id/product_name /sys/class/dmi/id/product_family /sys/class/dmi/id/board_name /sys/class/dmi/id/bios_version /sys/class/dmi/id/bios_date
  printf 'session: %s, desktop: %s, Hyprland: %s\n' "${XDG_SESSION_TYPE:-?}" "${XDG_CURRENT_DESKTOP:-?}" "$(hyprctl version 2>/dev/null | head -1)"

  section "Power supplies (battery, charger, charge limit)"
  for supply in /sys/class/power_supply/*; do
    [ -d "$supply" ] || continue
    printf '%s\n' "$supply"
    ( cd "$supply" && for name in type status present online capacity charge_control_start_threshold charge_control_end_threshold charge_behaviour charge_types cycle_count energy_full energy_full_design charge_full charge_full_design power_now current_now voltage_now current_max usb_type model_name manufacturer; do
        [ -r "$name" ] && printf '  %s = %s\n' "$name" "$(cat "$name" 2>/dev/null | tr '\n' ' ')"; done )
  done

  section "Performance profiles"
  show /sys/firmware/acpi/platform_profile /sys/firmware/acpi/platform_profile_choices /sys/firmware/acpi/pm_profile
  for entry in /sys/class/platform-profile/*; do [ -d "$entry" ] && show "$entry/name" "$entry/choices" "$entry/profile"; done
  command -v powerprofilesctl >/dev/null 2>&1 && powerprofilesctl list 2>&1 | head -20
  show /sys/devices/system/cpu/intel_pstate/status /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference /sys/devices/system/cpu/cpu0/cpufreq/energy_performance_available_preferences
  systemctl is-active power-profiles-daemon tlp tuned 2>/dev/null | paste -sd' ' | sed 's/^/services (power-profiles-daemon tlp tuned): /'

  section "Backlight and keyboard backlight"
  for light in /sys/class/backlight/* /sys/class/leds/*kbd* /sys/class/leds/*backlight*; do [ -d "$light" ] && show "$light/brightness" "$light/max_brightness"; done
  ls /sys/class/leds 2>/dev/null | paste -sd' ' | sed 's/^/leds: /'

  section "Xiaomi firmware interface (WMI) and platform drivers"
  # The Windows app talks to the firmware through WMI. These are the same interfaces as the kernel sees them.
  for device in /sys/bus/wmi/devices/*; do
    [ -d "$device" ] || continue
    printf '%s driver=%s %s\n' "$(basename "$device")" "$(basename "$(readlink "$device/driver" 2>/dev/null)" 2>/dev/null)" "$(cat "$device/object_id" "$device/notify_id" 2>/dev/null | paste -sd' ')"
  done
  ls /sys/bus/platform/drivers 2>/dev/null | grep -iE 'xiaomi|mi|wmi|ideapad|thinkpad|asus|huawei|redmi' | paste -sd' ' | sed 's/^/platform drivers: /'
  lsmod 2>/dev/null | grep -iE 'wmi|xiaomi|redmi|acpi_call|intel_hid|intel_vbtn|platform_profile|ucsi|thermal|dptf|int340' | awk '{print $1}' | paste -sd' ' | sed 's/^/modules: /'
  ls /sys/bus/acpi/devices 2>/dev/null | grep -iE 'MI|XM|TM|WM|PNP0C14|INT34|INTC' | paste -sd' ' | cut -c1-600 | sed 's/^/acpi devices: /'

  section "Sensors (temperature, fans, light, power meter)"
  for monitor in /sys/class/hwmon/hwmon*; do [ -d "$monitor" ] && printf '%s: %s (%s)\n' "$monitor" "$(cat "$monitor/name" 2>/dev/null)" "$(ls "$monitor" | grep -E '^(temp|fan|power|curr|in)[0-9]+_input$' | paste -sd' ')"; done
  for zone in /sys/class/thermal/thermal_zone*; do [ -d "$zone" ] && printf '%s: %s %s\n' "$zone" "$(cat "$zone/type" 2>/dev/null)" "$(cat "$zone/temp" 2>/dev/null)"; done
  ls -l /sys/class/powercap/intel-rapl:0/energy_uj 2>/dev/null
  for sensor in /sys/bus/iio/devices/iio:device*; do [ -d "$sensor" ] && printf '%s: %s (%s)\n' "$sensor" "$(cat "$sensor/name" 2>/dev/null)" "$(ls "$sensor" | grep -E 'illuminance|accel|proximity' | head -4 | paste -sd' ')"; done

  section "Display and input (Hyprland)"
  hyprctl monitors 2>/dev/null | grep -E '^Monitor|@|scale|availableModes' | cut -c1-400
  hyprctl devices 2>/dev/null | grep -E '^(mice|Keyboards|Tablets|Touch|Switches):|^\s+(Mouse|Keyboard|Touch Device|Tablet) at|^\s+[a-z0-9].*$' | head -60

  section "Special keys"
  echo "To see what the Mi, AI and function keys send, run in a terminal:  sudo libinput debug-events --show-keycodes   (or: wev)"
  ls /dev/input/by-path 2>/dev/null | paste -sd' ' | cut -c1-500

  section "Graphics and neural processor (for search and translation)"
  lspci -nnk 2>/dev/null | grep -A3 -iE 'vga|display|processing accelerators|neural' | cut -c1-200
  ls /dev/accel 2>/dev/null | paste -sd' ' | sed 's/^/\/dev\/accel: /'
  for package in intel-compute-runtime level-zero-loader intel-npu-driver intel-media-driver vulkan-intel; do pacman -Q "$package" 2>/dev/null || echo "$package: not installed"; done

  section "Programs the apps call"
  for program in python grim slurp wl-copy notify-send xdg-open brightnessctl powerprofilesctl hyprctl systemd-inhibit unison unison-fsmonitor ssh sshd tailscale quickshell qs caelestia; do have "$program"; done
  python - <<'PY' 2>&1
import importlib
for name, version in (("gi", None), ("Gtk", "4.0"), ("WebKit", "6.0"), ("Gtk4LayerShell", "1.0")):
    try:
        if version is None:
            importlib.import_module(name); print("python gi: ok")
        else:
            import gi; gi.require_version(name, version); print(f"python {name} {version}: ok")
    except Exception as error:
        print(f"python {name} {version or ''}: MISSING ({error})")
PY
  venv="${XDG_DATA_HOME:-$HOME/.local/share}/xiaomi-revamp/venv/bin/python"
  if [ -x "$venv" ]; then
    "$venv" - <<'PY' 2>&1
import sys
print("engine python:", sys.version.split()[0])
try:
    import openvino
    print("openvino", openvino.__version__, "devices:", openvino.Core().available_devices)
except Exception as error:
    print("openvino: MISSING or failing:", error)
PY
  else
    echo "engine environment: not created yet (run linux/install.sh)"
  fi
} 2>&1 | tee "$report"

printf '\nReport written to %s\n' "$report"

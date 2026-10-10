"""PC Manager's hardware backend for Linux: answers the same requests the Windows resident answers.

Purpose: the web pages ask for "state.read", "performance.set", "battery.limit" and so on. On Windows those go to
    Xiaomi's firmware interface. Here they go to what the Linux kernel and Hyprland expose: sysfs power supply and
    backlight files, the ACPI platform profile (or power-profiles-daemon), hyprctl for the display rate and input
    devices. A control the kernel does not offer on this laptop is reported as unavailable, never faked.
Dependencies: Python 3.11+. Optional programs: brightnessctl, powerprofilesctl, hyprctl, systemd-inhibit.
Outputs: preferences in ~/.config/xiaomi-revamp/pc-manager/settings.json.
Usage: imported by pc_manager.py. Self-check without hardware: python -B tests/check_backend.py
"""
import json
import socket
import subprocess
import time
from pathlib import Path

VERSION = "0.1.0-linux"
# Names the pages use for firmware modes, and the kernel or power-profiles-daemon profiles that stand in for them.
PROFILE_OF = {"Eco": ("low-power", "power-saver"), "Quiet": ("quiet",), "Auto": ("balanced",), "FullSpeed": ("performance",), "Turbo": ("performance",)}
PAGE_MODES = {"Online": ["Quiet", "Auto", "FullSpeed", "Eco"], "Offline": ["Eco", "Quiet", "Auto", "Turbo"]}
DEFAULTS = {"appearance": "system", "themePreset": "blue", "themeAccent": "#3482ff", "autoRefresh": False, "acRefreshRate": None,
            "batteryRefreshRate": None, "acMode": None, "batteryMode": None, "careLimit": 80, "chargeLimit": None, "travel": False,
            "touchpadEnabled": True, "touchscreenEnabled": True, "lastSource": None, "developerMode": False}


class Unavailable(Exception):
    """A control this laptop or this session does not offer; the message is shown to the user."""


class System:
    """File and program access, kept in one place so the checks can point it at a made-up tree."""

    def __init__(self, root="/", run=None, which=None):
        self.root = Path(root)
        self._run, self._which = run, which

    def path(self, relative):
        return self.root / relative.lstrip("/")

    def read(self, relative):
        try:
            return self.path(relative).read_text(encoding="utf-8", errors="replace").strip()
        except OSError:
            return None

    def number(self, relative):
        try:
            return int(self.read(relative))
        except (TypeError, ValueError):
            return None

    def write(self, relative, text):
        try:
            self.path(relative).write_text(str(text), encoding="utf-8")
        except PermissionError:
            raise Unavailable(f"No permission to write {relative}. Run linux/install.sh once to install the access rule.") from None
        except OSError as error:
            raise Unavailable(f"The kernel refused {relative}: {error.strerror or error}") from None

    def find(self, pattern):
        return sorted(self.root.glob(pattern.lstrip("/")))

    def has(self, program):
        if self._which is not None:
            return self._which(program)
        import shutil
        return shutil.which(program) is not None

    def run(self, arguments, timeout=8):
        if self._run is not None:
            return self._run(arguments)
        done = subprocess.run(arguments, capture_output=True, text=True, timeout=timeout)
        if done.returncode != 0:
            raise Unavailable(f"{arguments[0]} failed: {(done.stderr or done.stdout).strip()[:200]}")
        return done.stdout


class Preferences:
    def __init__(self, path):
        self.path = Path(path)
        self.values = dict(DEFAULTS)
        try:
            self.values.update(json.loads(self.path.read_text(encoding="utf-8")))
        except (OSError, ValueError):
            pass

    def __getitem__(self, key):
        return self.values.get(key)

    def set(self, **changes):
        self.values.update(changes)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_suffix(".pending")
        temporary.write_text(json.dumps(self.values, indent=2), encoding="utf-8")
        temporary.replace(self.path)


class Hardware:
    def __init__(self, system, preferences):
        self.system, self.preferences = system, preferences
        self._cpu = self._rapl = None

    # --- power -------------------------------------------------------------------------------------------
    def _supplies(self, kind):
        return [item for item in self.system.find("sys/class/power_supply/*") if (self.system.read(self._relative(item) + "/type") or "") == kind]

    def _relative(self, path):
        return str(path.relative_to(self.system.root)).replace("\\", "/")

    def battery(self):
        found = self._supplies("Battery")
        found = [item for item in found if self.system.read(self._relative(item) + "/present") != "0"]
        return self._relative(found[0]) if found else None

    def power_source(self):
        battery = self.battery()
        status = self.system.read(battery + "/status") if battery else None
        if status == "Discharging":
            return "Offline"
        online = [self.system.number(self._relative(item) + "/online") for kind in ("Mains", "USB") for item in self._supplies(kind)]
        if any(value == 1 for value in online) or status in ("Charging", "Full", "Not charging"):
            return "Online"
        return "Offline" if online and battery else "Unknown"

    def adapter_watts(self):
        """Rating of a USB-C charger from the Type-C power supply entry (volts times the current it offers)."""
        for item in self._supplies("USB"):
            base = self._relative(item)
            volts, amps = self.system.number(base + "/voltage_now"), self.system.number(base + "/current_max")
            if self.system.number(base + "/online") == 1 and volts and amps:
                return round(volts * amps / 1e12)
        return None

    def _energy(self, battery, name):
        """Watt-hours from energy_* (microwatt-hours), or from charge_* (microamp-hours) times the design voltage."""
        value = self.system.number(f"{battery}/energy_{name}")
        if value is not None:
            return value / 1e6
        charge, volts = self.system.number(f"{battery}/charge_{name}"), self.system.number(battery + "/voltage_min_design")
        return charge * volts / 1e12 if charge is not None and volts else None

    def charge_limit_file(self):
        battery = self.battery()
        relative = battery + "/charge_control_end_threshold" if battery else None
        return relative if relative and self.system.path(relative).exists() else None

    def set_charge_limit(self, percent, remember=True):
        relative = self.charge_limit_file()
        if relative is None:
            raise Unavailable("The kernel offers no charge limit for this battery (no charge_control_end_threshold).")
        if not 40 <= percent <= 100:
            raise ValueError("Charge limit must be between 40 and 100.")
        self.system.write(relative, percent)
        if self.system.number(relative) != percent:
            raise Unavailable(f"The firmware did not accept {percent}%.")
        if remember:
            self.preferences.set(chargeLimit=percent, travel=False, **({"careLimit": percent} if percent < 100 else {}))

    def set_travel(self, on):
        """One charge to 100%; the policy check restores the care limit when the battery is full or unplugged."""
        care = self.preferences["careLimit"] or 80
        self.set_charge_limit(100 if on else care, remember=False)
        self.preferences.set(travel=bool(on), chargeLimit=care)

    # --- display -----------------------------------------------------------------------------------------
    def _backlight(self):
        found = self.system.find("sys/class/backlight/*")
        preferred = [item for item in found if item.name == "intel_backlight"] or found
        return self._relative(preferred[0]) if preferred else None

    def brightness(self):
        base = self._backlight()
        now, most = (self.system.number(base + "/brightness"), self.system.number(base + "/max_brightness")) if base else (None, None)
        return max(1, round(100 * now / most)) if now is not None and most else None

    def set_brightness(self, percent):
        base = self._backlight()
        if base is None:
            raise Unavailable("No backlight device was found.")
        percent = max(1, min(100, int(percent)))
        if self.system.has("brightnessctl"):
            self.system.run(["brightnessctl", "-q", "-d", base.rsplit("/", 1)[1], "set", f"{percent}%"])
        else:
            self.system.write(base + "/brightness", max(1, round(self.system.number(base + "/max_brightness") * percent / 100)))
        return self.brightness()

    def _monitor(self):
        if not self.system.has("hyprctl"):
            return None
        try:
            monitors = json.loads(self.system.run(["hyprctl", "-j", "monitors"]))
        except (Unavailable, ValueError, OSError):
            return None
        internal = [item for item in monitors if str(item.get("name", "")).startswith("eDP")]
        return (internal or monitors or [None])[0]

    def refresh(self):
        monitor = self._monitor()
        if monitor is None:
            return None, []
        size = f"{monitor.get('width')}x{monitor.get('height')}@"
        rates = sorted({round(float(mode[len(size):].rstrip("Hz"))) for mode in monitor.get("availableModes", []) if mode.startswith(size)})
        return round(float(monitor.get("refreshRate", 0))) or None, rates

    def set_refresh(self, hertz):
        monitor = self._monitor()
        current, rates = self.refresh()
        if monitor is None or hertz not in rates:
            raise Unavailable(f"{hertz} Hz is not offered by this display.")
        if current == hertz:
            return hertz
        rule = f"{monitor['name']},{monitor['width']}x{monitor['height']}@{hertz},{monitor.get('x', 0)}x{monitor.get('y', 0)},{monitor.get('scale', 1)}"
        self.system.run(["hyprctl", "keyword", "monitor", rule])
        return hertz

    def cycle_refresh(self):
        current, rates = self.refresh()
        if len(rates) < 2:
            raise Unavailable("This display offers one refresh rate.")
        return self.set_refresh(rates[(rates.index(current) + 1) % len(rates)] if current in rates else rates[0])

    # --- performance -------------------------------------------------------------------------------------
    def _profiles(self):
        """(current, choices, setter): power-profiles-daemon when it runs, else the ACPI platform profile file."""
        if self.system.has("powerprofilesctl"):
            try:
                current = self.system.run(["powerprofilesctl", "get"]).strip()
                listed = self.system.run(["powerprofilesctl", "list"])
                choices = [name for name in ("power-saver", "balanced", "performance") if name + ":" in listed]
                return current, choices, lambda name: self.system.run(["powerprofilesctl", "set", name])
            except (Unavailable, OSError):
                pass
        # Kernel 6.14 and later: one entry per driver under /sys/class/platform-profile. Before that, and still
        # present as the sum of all drivers, the two files under /sys/firmware/acpi.
        entries = self.system.find("sys/class/platform-profile/*/profile")
        for target, listing in [(self._relative(entry), self._relative(entry.parent) + "/choices") for entry in entries] + [("sys/firmware/acpi/platform_profile", "sys/firmware/acpi/platform_profile_choices")]:
            current, choices = self.system.read(target), (self.system.read(listing) or "").split()
            if current and choices:
                return current, choices, lambda name, target=target: self.system.write(target, name)
        return None, [], None

    def mode(self, source):
        current, choices, _ = self._profiles()
        visible = [name for name in PAGE_MODES.get(source, PAGE_MODES["Offline"]) if any(profile in choices for profile in PROFILE_OF[name])]
        active = next((name for name in visible if current in PROFILE_OF[name]), None)
        return active, visible

    def set_mode(self, name, source=None, remember=True):
        _, choices, setter = self._profiles()
        profile = next((item for item in PROFILE_OF.get(name, ()) if item in choices), None)
        if setter is None or profile is None:
            raise Unavailable(f"{name} is not offered on this system.")
        setter(profile)
        if remember and self.preferences["acMode" if (source or self.power_source()) == "Online" else "batteryMode"] is not None:
            self.preferences.set(**{"acMode" if (source or self.power_source()) == "Online" else "batteryMode": name})

    # --- input -------------------------------------------------------------------------------------------
    def _device(self, kind):
        if not self.system.has("hyprctl"):
            return None
        try:
            devices = json.loads(self.system.run(["hyprctl", "-j", "devices"]))
        except (Unavailable, ValueError, OSError):
            return None
        if kind == "touchscreen":
            names = [item.get("name") for item in devices.get("touch", [])]
        else:
            names = [item.get("name") for item in devices.get("mice", []) if "touchpad" in str(item.get("name", "")).lower()]
        return names[0] if names else None

    def set_input(self, kind, on):
        name = self._device(kind)
        if name is None:
            raise Unavailable(f"No {kind} was found by Hyprland.")
        self.system.run(["hyprctl", "keyword", f"device[{name}]:enabled", "true" if on else "false"])
        self.preferences.set(**{kind + "Enabled": bool(on)})

    # --- snapshot ----------------------------------------------------------------------------------------
    def read(self):
        system, preferences = self.system, self.preferences
        battery, source = self.battery(), self.power_source()
        mode, visible = self.mode(source)
        rate, rates = self.refresh()
        limit_file = self.charge_limit_file()
        limit = system.number(limit_file) if limit_file else None
        wanted = 100 if preferences["travel"] else preferences["chargeLimit"]
        full, design = (self._energy(battery, "full"), self._energy(battery, "full_design")) if battery else (None, None)
        vendor, product = system.read("sys/class/dmi/id/sys_vendor"), system.read("sys/class/dmi/id/product_name")
        bios = " · ".join(filter(None, [system.read("sys/class/dmi/id/bios_version"), system.read("sys/class/dmi/id/bios_date")]))
        return {
            "deviceName": socket.gethostname().upper(), "specs": None, "model": " ".join(filter(None, [vendor, product])) or None, "bios": bios or None,
            "serial": None, "diagnosticsLoading": False, "firmwareAvailable": mode is not None or limit is not None,
            "firmwareError": None if mode is not None or limit is not None else "This kernel exposes no performance profile and no charge limit for this laptop. Brightness and display controls remain available.",
            "mode": mode, "chargeLimit": limit, "requestedChargeLimit": wanted if limit is not None else None,
            "chargeConflict": limit is not None and wanted is not None and limit != wanted, "modeConflict": False, "wantedMode": None,
            "visibleModes": visible, "modeLabel": mode, "testMode": False, "adapterWatts": self.adapter_watts() if source == "Online" else None,
            "batteryPercent": system.number(battery + "/capacity") if battery else None, "powerSource": source,
            "batteryHealth": round(100 * full / design) if full and design else None, "cycles": system.number(battery + "/cycle_count") if battery else None,
            "designWh": design, "fullWh": full, "travel": bool(preferences["travel"]), "careLimit": preferences["careLimit"],
            "brightness": self.brightness(), "refreshRate": rate, "refreshRates": rates,
            "touchpadEnabled": preferences["touchpadEnabled"] if self._device("touchpad") else None,
            "touchscreenEnabled": preferences["touchscreenEnabled"] if self._device("touchscreen") else None,
            "hapticsAvailable": False, "vibration": None, "pressure": None,
            "preferences": {
                "appearance": preferences["appearance"], "closeToTray": True, "minimizeToTray": False, "autoRefresh": bool(preferences["autoRefresh"]),
                "themePreset": preferences["themePreset"], "themeAccent": preferences["themeAccent"], "themeBackground": None, "themeSurface": None, "themePalettes": [],
                "developerMode": bool(preferences["developerMode"]), "originalPopupEnabled": False, "profileImage": None, "popupScale": 100, "compactPanel": False,
                "popupAnimations": True, "osdStyle": "revamp", "customAssets": {}, "acRefreshRate": preferences["acRefreshRate"] or (rates[-1] if rates else None),
                "batteryRefreshRate": preferences["batteryRefreshRate"] or (rates[0] if rates else None), "acMode": preferences["acMode"], "batteryMode": preferences["batteryMode"],
                "powerProfiles": preferences["acMode"] is not None or preferences["batteryMode"] is not None, "chargeGuard": (preferences["chargeLimit"] or 100) < 100,
                "touchpadFeature": True, "touchscreenFeature": True, "refreshFeature": len(rates) > 1, "owlFeature": False,
                "rememberBrightness": False, "autoBrightness": False, "brightnessCap": False,
            },
            "customization": self.customization(),
        }

    def customization(self):
        return {"popupPosition": "bottom-right", "popupOffsetX": 0, "popupOffsetY": 0, "quickLinks": [], "quickSystemActions": ["screenoff", "awake", "refresh", "autorefresh", "touchpad", "touchscreen", "sleepoff"], "quickIcons": {}}

    # --- telemetry ---------------------------------------------------------------------------------------
    def telemetry(self, battery_only=False):
        system, battery = self.system, self.battery()
        status = system.read(battery + "/status") if battery else None
        microwatts = system.number(battery + "/power_now") if battery else None
        if microwatts is None and battery:  # some batteries report current and voltage instead
            amps, volts = system.number(battery + "/current_now"), system.number(battery + "/voltage_now")
            microwatts = amps * volts / 1e6 if amps is not None and volts else None
        watts = abs(microwatts) / 1e6 if microwatts else None
        if watts is not None and status == "Discharging":
            watts = -watts  # the pages expect consumption as a negative number, as on Windows
        # Bits as in the Windows battery state the pages test: 1 on mains, 2 discharging, 4 charging.
        state = (1 if self.power_source() == "Online" else 0) | (2 if status == "Discharging" else 0) | (4 if status == "Charging" else 0)
        result = {"batteryWatts": watts, "batteryPowerState": state if battery else None}
        if battery_only:
            return result
        fields = (system.read("proc/stat") or "").splitlines()[:1]
        cpu = None
        if fields:
            numbers = [int(value) for value in fields[0].split()[1:]]
            sample = (sum(numbers), numbers[3] + (numbers[4] if len(numbers) > 4 else 0))
            if self._cpu and sample[0] > self._cpu[0]:
                cpu = 100 * (1 - (sample[1] - self._cpu[1]) / (sample[0] - self._cpu[0]))
            self._cpu = sample
        memory = {line.split(":")[0]: int(line.split()[1]) for line in (system.read("proc/meminfo") or "").splitlines() if ":" in line and line.split()[1].isdigit()}
        total, free = memory.get("MemTotal"), memory.get("MemAvailable")
        package = None
        energy = system.number("sys/class/powercap/intel-rapl:0/energy_uj")  # readable by root only on most kernels
        if energy is not None:
            now = time.monotonic()
            if self._rapl and now > self._rapl[1] and energy >= self._rapl[0]:
                package = (energy - self._rapl[0]) / 1e6 / (now - self._rapl[1])
            self._rapl = (energy, now)
        zones = [system.number(self._relative(item) + "/temp") for item in system.find("sys/class/thermal/thermal_zone*")]
        degrees = max((value / 1000 for value in zones if value and 0 < value < 130000), default=None)
        result.update({
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "cpuPercent": cpu, "gpuPercent": None, "gpuWatts": None, "gpuMHz": None,
            "memoryPercent": 100 * (total - free) / total if total and free is not None else None,
            "memoryUsedGiB": (total - free) / 2**20 if total and free is not None else None, "memoryTotalGiB": total / 2**20 if total else None,
            "batteryRateNative": microwatts, "cpuPackageWatts": package, "temperatureC": degrees, "temperatureSource": "thermal zones" if degrees else "none",
            "npuPercent": None, "fanRpm": None,
        })
        return result

    # --- policies (called every 30 seconds by the host) ---------------------------------------------------
    def apply_policies(self):
        """Things that follow the power source: display rate, performance mode, the end of a travel charge."""
        preferences, source, done = self.preferences, self.power_source(), []
        if source == "Unknown":
            return done
        if preferences["travel"]:
            battery = self.battery()
            if source == "Offline" or (battery and (self.system.number(battery + "/capacity") or 0) >= 100):
                try:
                    self.set_travel(False)
                    done.append("Travel charging ended; the care limit is back.")
                except Unavailable:
                    pass
        if preferences["lastSource"] != source:
            online = source == "Online"
            wanted_rate = preferences["acRefreshRate" if online else "batteryRefreshRate"]
            if preferences["autoRefresh"] and wanted_rate:
                try:
                    if self.refresh()[0] != wanted_rate:
                        self.set_refresh(wanted_rate)
                        done.append(f"Refresh rate {wanted_rate} Hz")
                except Unavailable:
                    return done  # the display was not ready: the change of source stays open for the next check
            wanted_mode = preferences["acMode" if online else "batteryMode"]
            if wanted_mode:
                try:
                    self.set_mode(wanted_mode, source, remember=False)
                except Unavailable:
                    pass
            preferences.set(lastSource=source)
        return done


class Router:
    """Maps the pages' requests to the hardware. Window requests (hide, minimize...) are handled by the host."""

    def __init__(self, hardware):
        self.hardware = hardware

    def handle(self, method, args):
        hardware, preferences = self.hardware, self.hardware.preferences
        if method == "state.read":
            return {"hardware": hardware.read(), "telemetry": hardware.telemetry(), "xiaomiPath": None}
        if method == "quick.read":
            return {"hardware": hardware.read(), "telemetry": hardware.telemetry(battery_only=True)}
        if method == "telemetry.read":
            return hardware.telemetry()
        if method == "window.live":
            rate, _ = hardware.refresh()
            battery = hardware.battery()
            return {"batteryPercent": hardware.system.number(battery + "/capacity") if battery else None, "powerSource": hardware.power_source(), "brightness": hardware.brightness(), "refreshRate": rate}
        if method == "settings.read":
            return {"rows": [], "keyApps": {}, "alsAvailable": False}
        if method == "settings.customization":
            return hardware.customization()
        if method == "settings.history":
            return {"canUndo": False, "canRedo": False}
        if method == "suite.read":
            return {"enabled": False}
        if method == "xiaomi.components":
            return {"manager": None, "store": None, "ai": None}
        if method == "display.brightness":
            return {"brightness": hardware.set_brightness(args["value"])}
        if method == "display.refresh":
            hardware.set_refresh(int(args["value"]))
        elif method == "display.cycle":
            hardware.cycle_refresh()
        elif method == "display.automatic":
            preferences.set(autoRefresh=bool(args["on"]), acRefreshRate=int(args["ac"]), batteryRefreshRate=int(args["battery"]), lastSource=None)
            hardware.apply_policies()
        elif method == "performance.set":
            hardware.set_mode(args["value"], args.get("source"))
        elif method == "performance.profiles":
            preferences.set(acMode=args.get("ac"), batteryMode=args.get("battery"), lastSource=None)
            hardware.apply_policies()
        elif method == "battery.limit":
            hardware.set_charge_limit(int(args["value"]))
        elif method == "battery.care":
            hardware.set_charge_limit((preferences["careLimit"] or 80) if args["on"] else 100)
        elif method == "battery.travel":
            hardware.set_travel(bool(args["on"]))
        elif method == "input.enabled":
            hardware.set_input(args["device"], bool(args["on"]))
        elif method == "settings.appearance":
            preferences.set(appearance={"light": "light", "dark": "dark"}.get(args.get("appearance"), preferences["appearance"]), themePreset=args.get("preset") or preferences["themePreset"],
                            themeAccent=args.get("accent") or preferences["themeAccent"], developerMode=bool(args.get("developerMode")))
        else:
            raise Unavailable(f"Not available on Linux yet ({method}).")
        return {"message": "Saved."}

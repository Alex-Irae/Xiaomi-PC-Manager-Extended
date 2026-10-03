# XiControl reused controls and settings

Source: the user-supplied `XiaomiPCManager/XiControl-0.16.0` directory, version 0.16.0. License: GPLv3, preserved in this directory and the project root. The original codebase is not modified.

Copied modules retain XiControl namespaces: Config, Input, SystemIntegration, Wmi, localization, the command/controller layer, policy guards, complete advanced settings pages, OSD/monitor/tray metrics, and required SVG/WAV assets. The original Program, TrayApp, and dark QuickPanelForm are not imported. The primary Xiaomi popup and secondary HTML manager belong to this project.

Local adaptations:

- `Log.cs` writes timestamped logs under this app's local-data directory and honors the logging setting.
- `AppPaths` and the host config store place preferences in `%LOCALAPPDATA%\XiaomiAIManager\settings.json`; imported settings are nested in its `XiControl` property.
- `RefreshRate.Current()` provides actual internal-panel readback. The cycle and automatic source-transition paths are connected. Display/startup/same-source resume enforcement was removed so manual selections persist; HoldRefreshRate is retained only for old configuration compatibility.
- `AdvancedControls` mounts every required SettingsActions callback, events, key gestures, monitoring, notifications, and the optional API. MIFS and haptics are shared with the popup. The hardware service retains durable travel-charge recovery, clears travel on startup and preserves a normal full-charge choice.
- The host now exposes controller settings in its own categorized English UI; imported native settings remain implementation/reference. Normal daily setup enables firmware routing, with Mi to popup and Settings/F9 to Windows Settings, and AI/F7 to the newest installed XiaoAI. All key actions are user-remappable. Test routing, component update checks and networking default off.
- Stay-awake display and AC lid changes are opt-in, with durable original-plan recovery. Startup and reconfiguration reflect failure to enable stay awake.
- Scheduled tasks, API settings directory, firewall rule, and task XML filename use this app's name, preserving original XiControl configuration/tasks.
- HTTP body reads have a byte ceiling even for chunked requests and a deadline. Advanced dead-zone/edge-application failures notify the host.
- Release checks compare the reused XiControl component version 0.16.0 with upstream; they do not update this app or Xiaomi drivers.
- KeyRouter has host callbacks for validated manager pages and native-picked app bindings. Settings always uses its configured mapping, including when the popup is open; Task view is routed as an action as well as a physical event.
- ChargeGuard delegates full/travel and care intent to the shared host policy. Diagnostic generations prevent old background input/haptic reads replacing confirmed writes.
- Confirmed manual mode selections update separate AC/battery slots while profiles are enabled. The host enables this strategy once for the user's chosen behavior; an unseen source is not guessed. Automatic mode writes validate the current source and saved slot under the shared transport lock. A refusal retains the saved profile and notifies instead of silently applying Auto. Source events refresh visibility and the web windows.

The Svg 3.4.7 dependency and embedded resource names match the supplied source. No package installation or restore was performed. `docs/SETTINGS_COVERAGE.md` maps the integration and evidence limits.

Upstream Russian source comments and diagnostic strings may remain in native logs; the new user interface and errors are English. Hardware support and driver ABI limitations remain those of the copied modules. Copying source does not establish compatibility with this user's laptop.

- Firmware Fn+K readback updates the active AC/battery profile without writing a second mode. Fn+S routes through the cycle callback. App-owned input toggles no longer use imported confirmation dialogs.
- Stock Xiaomi OSD routing supplements the optional original XiControl OSD. Backlight automatic value is 0x80; Fn/Windows lock events use their actual OSD kinds.
- Host Windows shortcuts are managed separately through RegisterHotKey; startup trigger delays are zero. Original input/charge transport and monitor modules remain in use.
- The quick display-rate button and keyboard callbacks share `HardwareService.CycleRefresh`, retaining the original rate-selection backend. Firmware event delivery is still hardware-specific; ordinary S/K input is not intercepted to imitate Fn.
- Host settings apply through the original validated callbacks. Bounded manager Undo/Redo retains preferences, shared policy strategy and changed hardware readbacks without adding another controller process.
- Default OSD uses supplied dark Xiaomi English artwork with white glyphs, independently of the manager theme. Missing stock Travel and unlock cards have local matching SVG variants. Original source/assets remain preserved.
- Screen off uses the Windows monitor-power command; it is independent of XiControl's Stay awake policy and never requests system sleep.

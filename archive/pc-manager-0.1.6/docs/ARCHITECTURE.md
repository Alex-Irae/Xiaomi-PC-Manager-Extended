**Historical reconnaissance:** this document preserves the original design and source-boundary investigation. Current startup, key mapping, own settings and live evidence are described in README.md and RUNTIME_TEST_REPORT.md. Its old source-only/no-key-routing statements are not current acceptance results.

# Xiaomi AI PC Manager: reconnaissance and implementation architecture

Inspection date: 2026-09-26. Inputs: the supplied Xiaomi PC Manager 5.8.1.121 folder, its existing GitHub English patch, XiControl 0.16.0 source, and the project handoff. The patch's repository and exact commit were not supplied. This report describes these local files, not an upstream release.

Updated 2026-09-27 after the user's screenshots and correction: the primary product is a compact white Windows quick panel, modeled on Xiaomi's small popup, with XiControl functions operated inside it. The larger manager is secondary. The attached handoff is background specification; the user's explicit popup-first request takes precedence over its earlier shell framing. The English OEM patch is preserved rather than replaced.

Current launch: a tray resident hosts a 420 x 680 logical-pixel borderless panel and a separate larger window. `--toggle` is the default and future keyboard-binding target, `--tray` starts hidden, `--manager` opens the secondary window, and `--monitor-small/medium/large` opens the original XiControl monitor views. `--test` uses a distinct resident and local settings. No keyboard mapping is installed. The executable manifest uses `asInvoker`; first startup elevates the hardware resident explicitly. Subsequent unelevated launches send five allowlisted window-activation messages, with no hardware command payload. Release compilation and static checks pass; UI/hardware behavior has not been runtime validated.

## 1. Xiaomi PC Manager architecture and directory map

The application is a hybrid, not a standalone web application. `XiaomiPcManager.runtimeconfig.json` identifies a self-contained .NET 6.0.36 application. WinUI assemblies, compiled XAML (`.xbf`), PRI resources, native plugins, and WebView2 coexist. The web renderer identifies React DOM 17.0.2. The bundle contains React Router, Redux Toolkit/Immer, Ant Design styles, CSS modules, SVG sprites, and Lottie animations.

Paths below are relative to `../5.8.1.121/`.

| Path | Observed contents and role |
| --- | --- |
| `dist/index.html` | Main web entry, root and modal-root containers, MiSans font declarations, CSS and JS references. Its Chinese title/lang metadata survives the English patch. |
| `dist/static/js/main.js` | Main UI, English literals, routes, state slices, bridge calls, embedded SVG symbols and animations; approximately 2.5 MB. No frontend source tree or source map was found here. |
| `dist/static/css/main.css` | Ant Design styles and Xiaomi custom CSS modules; approximately 748 KB. |
| `dist/static/media/` | Interconnect illustrations, account imagery, QR graphics, special-key graphics. Some baked-in text may remain Chinese. |
| `Assets/font/` | MiSans Regular (400), Medium (500), Demibold (600). |
| `Assets/`, `res/`, `skin/`, `Styles/`, `UserControls/`, `PopWindows/` | Native graphics, DPI/theme variants, skin assets and compiled UI resources. |
| `Pages/` | Native WebContainerPage, OTAPage, privacy, app lists, voiceprint recording, and other compiled XAML pages. |
| `XiaomiPcManager.exe`, `.dll`, `.deps.json`, `.runtimeconfig.json` | Native/.NET entry and application logic. |
| `XiaomiPcHost.exe`, `SvrCModule.dll`, `SvrCModuleClrWrapper.dll`, `NativeApiWrapper.dll`, `PcCSharpIPC.dll`, `PipeClrWrapper.dll` | Host and interop/IPC candidates. Filenames and bundle calls establish their presence, not a public callable ABI. |
| `Plugins/`, `rubbish_cleanup/` | OEM cleanup, startup optimization, application repair, gesture and network plugins. Includes CleanerEngine, CleanerProxy, PluginJunkCleaner, PluginSystemBoost and Qt SQL support. |
| `data/`, `sqldrivers/` | Local data and SQLite support. Rubbish databases/templates are present. These are vendor implementation data, not our settings database. |
| `Drivers/`, `icc/` | Driver/install resources and display profiles. Keep OEM-managed. |
| `handoff/`, `PcClipboard/`, `PcControlCenter/` | Interconnect service and native clipboard/control-center components. |
| `Search/`, `AISearchSDK.dll`, `aisearchservice.dll`, `SubtitleTranscriptor/` | Proprietary search and transcription integration. Presence does not establish an offline English AI API. |
| `XaAppStore.exe`, `StorePlugs.dll`, `AppStoreCPlugs.dll`, `AppStoreHPlugs.dll` | OEM store components. |
| `MiScreenShare.exe`, `MiSmartShare*.exe`, `dist_service.exe`, `DistributedService.exe`, `SambaServer.exe`, `MiPlayCastService.exe` | Sharing/service executable candidates. Their CLI contracts are unverified. |
| `OSDLauncher.exe`, `OSDUtility.exe`, `OSD_resource.7z` | OEM on-screen display implementation. |
| `Update.dll`, `MiService2_Setup.exe`, `AIService_Setup.exe`, `DeleteDriver.exe`, install CMD files | OEM update/install operations. Do not execute as generic frontend actions. |
| `zh-CN/`, `zh-Hans/`, `resources.pri` | Native localization/resources. The English JS patch does not prove that native dialogs are translated. |
| `Channel.ini`, `micfg.ini`, `MiDisFre.ini`, `meta_data.xml`, `.properties`, `.log` | Configuration, metadata and bundled logging artifacts. |
| Root `../.run`, `../Launch.exe` | `.run` selects 5.8.1.121; launcher exists beside the version folders. 5.8.0.57 contains only a few residual files in this snapshot. |

## 2. XiControl architecture

`src/XiControl.csproj` targets .NET 8 Windows, x64, WinForms. Its manifest requests administrator privileges. The normal program is one elevated tray resident, not a Windows service. Firmware access uses `System.Management`, `root\\wmi:MiCommonInterface.MiInterface`, 32-byte MIFS messages, and `HID_EVENT20` events. The source explicitly avoids direct EC access and third-party kernel drivers.

| Layer | Files to inspect/reuse |
| --- | --- |
| Composition | `src/Program.cs`, dependency injection, single instance |
| Hardware protocol | `src/Wmi/{Mifs,IMifsClient,MifsClient,MifsDialect}.cs` |
| Firmware events | `src/Wmi/{IKeyEventSource,MifsEventWatcher}.cs`, `src/Input/{KeyRouter,KeyMap,MiButtonGesture}.cs` |
| Windows display | `SystemIntegration/{Brightness,RefreshRate,WmiQuery}.cs` |
| Haptic touchpad | `SystemIntegration/{TouchpadHaptics,TouchpadHapticsProtocol}.cs`: BLTP7853 vendor HID collection, descriptor checks, serialized operations, readback |
| Input device enable/disable | `HidNodeToggle.cs` with thin TouchpadControl/TouchscreenControl config adapters. Protects bus/controller nodes and tracks persistent disable recovery. |
| Telemetry | `PowerStatus`, `BatteryInfo`, `PowerDraw`, `GpuTelemetry`, `TrayMetrics`, `SystemInfo` |
| Automatic behavior | `ChargeGuard`, `PowerProfileGuard`, `RefreshRateGuard`, `BrightnessCapGuard`, `AutoBrightnessGuard`, `TravelChargeMonitor` |
| HTTP | `HttpApi`, `ApiRouter`, `ApiSettings`; optional, disabled by default |
| UI-only | TrayApp, QuickPanelForm, SettingsForm/tabs, MonitorForm, SvgIcons, OSD/flyout forms. Do not bring these into the web component system. |
| Command layer | `Ui/AppController.cs`: useful behavior reference, but contains UI/notification/config concerns that need separation. |
| Configuration/localization | Config JSON, portable settings, embedded `Localization/lang/en.json`. Keep our settings separate. |

XiControl 0.16.0 is supplied under GPLv3. Preserve its license, provenance and source when reusing it. Do not treat Xiaomi graphics/fonts as independently licensed assets for public redistribution.

## 3. Xiaomi visual system

At 910 px viewport width, the bundle sets `1rem = 20px`, scaled proportionally with window width. Reproduce the appearance with readable, bounded dimensions rather than scaling all text indefinitely.

| Element | Source observation | New component |
| --- | --- | --- |
| Font | MiSans 400/500/600; body fallback Arial | Local-font option plus Segoe UI fallback |
| Sidebar | `side-bar--_cBHu`, background `#eff4f8`; vertically stacked icon/label navigation | MiSidebar |
| Selected navigation | `nav-btn--2KP09.active--3L0zn`: `#3482ff`, translucent white background | Navigation button with selected state |
| Primary controls | `#3482ff`, hover `#317af0`, active `#2e73e0`; secondary controls also use `#0d84ff` | MiButton, MiSegmentedControl |
| Text | `#333`, `#666`, `#999`; typography .6/.7/.8/1/1.2 rem | 12/14/16/20/24 px text scale |
| Cards | White surfaces, subtle border, `.6rem` rounded corners | MiCard, MiDeviceCard |
| Radii | Small .2rem; large .6rem; pill 1rem | 4/12/20 px |
| Switch | 2rem × 1.1rem track, .8rem thumb, .3s transition | Accessible checkbox-backed MiToggle |
| Spacing | .2/.3/.4/.6/.8/1/1.2 rem recurring | 4/6/8/12/16/20/24 px |
| Shadow | Subtle black-alpha elevation; dialogs stronger than cards | Dialog/toast shadow |
| Icons | Bundle SVG sprite names include `nav_computer`, `nav_toolsbox`, `battery`, `cpu`, `display`, `ram`, `setting_touchpad` | Clean inline SVG icons, no giant bundle dependency |
| Animation | .2/.3s interaction transitions, Lottie feature illustrations | Short transitions; reduced-motion support |

English is the product language. The existing patch is the reference for terminology, not a translation workload to restart. Our own labels, statuses, dialogs, errors and tooltips must be English. OEM dialogs opened externally may still contain Chinese.

## 4. Routes, deep links and external launch evidence

| Feature | Confirmed internal route/identifier | External invocation status |
| --- | --- | --- |
| Home/interconnect | `/suggest`, `/deviceConnect` | Internal web routes only |
| Computer info | `/computer` | Internal only |
| Toolbox | `/systemOptimization`, `/toolsBox` | Internal only; `register_open_system_optimization` is a native event subscription |
| Battery/performance | `/systemOptimization/battery-performance`; intent OpenBatteryPerformance = 18 | Internal only |
| Drivers | `/systemOptimization/driver`; intent OpenDriverPage = 8, ScanDrivers = 3 | `--scan_driver` parser and activation handler verified statically; `popup_driver_manager` bridge method exists |
| Cleaner | `/systemOptimization/cleanup`; ScanJunkFiles = 21, CleanupJunkFiles = 22 | Internal only |
| Optimization | `/systemOptimization/speedup` | Internal only |
| Store | Nav item has no route; `open_app_store`, `get_app_store_state`; XaAppStore.exe exists | Executable presence verified, launch behavior not tested |
| Support | `/service`, `/feedbackHistory`; native WebOnlineServicePage exists | Internal only |
| Settings | `/setting`; `register_open_set_page` subscription | Internal only |
| Personalization | `/personalize` | Internal only |
| Meeting assistant | `/meetingAssistant` | Internal only |

Static UTF-16 strings in `XiaomiPcManager.dll` identify `ProcessActivateCommandLine`, `--coldboot`, `--activate_service`, `XiaomiPCManager_SingleInstance_Pipe_`, `PostToWebIntent` and another pipe path `\\\\.\\pipe\\EEE5A3CD-797C-4795-9794-667AA868AC07`. These establish activation/IPC mechanisms but not the serialization contract, pipe ACLs, full pipe name, accepted page arguments or a custom URI registration. Do not fabricate a URI scheme, send guessed pipe payloads, or pass internal React paths as CLI arguments.

Updated forwarding uses the verified driver flag and visible English navigation described below. Final-page confirmation and standalone driver hosting remain unimplemented. Registry discovery does not register protocols or modify OEM files.

## 5. Native bridge and API map

### Verified driver command and Toolbox boundary, 2026-09-27

Static PE metadata and IL inspection used `System.Reflection.Metadata`, without loading or executing Xiaomi assemblies. `Options.IsScanDriver` has the CommandLine option name `scan_driver`. `MainHelper.ParseCommandLineToArgs` maps it to `CommandArgs.IsScanDriver`; `ProcessActivateCommandLine` calls `WebViewService.HandleScanDriverBusiness`. The latter creates intent 3 and its dispatcher callback posts `register_intent`. Cold-start `ProcessCommandLine` sets `WebViewService.ScanDriver`. This supports a fixed `--scan_driver` request against the supplied 5.8.1.121 executable. It does not prove arrival, scanning completion, driver health, or behavior in other releases.

`Options.PageIntent` is `-i` / `--intent`, but `CmmWrapperUIService.HandleCMD` accepts only 13, 14, 15, 17 and 38 in the inspected flow. Frontend `OpenToolBox = 20` is therefore not a verified CLI route. `PcControlCenterService.OpenClientSystemOptimization` instead shows the OEM window and sends `register_open_system_optimization` internally. No guessed `--intent 20` or pipe command is used.

`DriverManagementPopup.dll` is a native PE without CLR metadata. `PopWindow.HandlePopWindowBusiness` dispatches `popup_driver_manager` to its owner's `DriverPopUp` method. No standalone driver executable or verified external DLL-host contract was found. This implementation leaves that plugin with its original owner and still activates Xiaomi PC Manager.

For Toolbox and Settings, `XiaomiBridge` invokes visible English controls using native Windows UI Automation on the capability worker. It filters top-level windows by PID, and verifies that PID's actual executable path against the selected `XiaomiPcManager.exe`. Fixed label sequences cover Toolbox, maintenance cards, and the screenshot settings categories. No coordinate click, arbitrary selector from the webview, or proprietary bridge injection is used. Labels depend on the supplied English patch. Provider failures yield a destination hint; accepted Invoke/Select does not certify page arrival, so `exactPage` remains false. A briefly activated OEM home window is possible.

The platform behavior is documented in Microsoft's [UI Automation control patterns](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-control-patterns-overview) and [InvokePattern API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.invokepattern.invoke?view=windowsdesktop-10.0). These references support API selection, not a tested Xiaomi navigation result. The existing Windows Desktop framework supplies the assemblies through a WPF framework reference; the host remains WinForms.

The bundle's `L(method, params)` wrapper sends a WebView2 message:

```json
{"mode":0,"data":{"id":1,"persistent":false,"request":{"method":"get_charging_threshold","params":{}}}}
```

`R(method, onSuccess, onFailure, params)` uses persistent subscriptions. Unregister uses mode 1 and the request ID. Responses correlate by ID and contain `response.code`, `response.data`, `response.message`. Code 0 is bridge success. Individual operations may additionally return `result` or `state`. The bundle has a CEF `window.cefQuery` fallback, but final bootstrap enables WebView2. These calls require Xiaomi's native dispatch handlers; copying JS into our webview does not make them available.

| Domain | Observed native methods and parameters |
| --- | --- |
| Battery | `get_battery_info`, `get_battery_original_info`, `get_battery_health_status`, `get_charging_mode`, `get_charging_protect`, `get_charging_threshold`, `set_charging_protect({mode})`, `set_charging_threshold`, `is_support_hyper_charging`, `register_battery_percentage`, `register_battery_notify`, `register_ac_power_status` |
| Performance | `get_workLoad_mode`, `set_workLoad_mode({mode})`, `register_workLoad_mode_change`, `get_workLoad_mode_decepticon_enable`, `get_turbo_engine_enable`, `set_turbo_engine`, smart acceleration subscriptions |
| Display | `get/set_screen_brightness`, `get/set_screen_ratio`, `get/set_screen_gamut`, `get_screen_hdr`, `get/set_eye_protection`, `get/set_eye_protection_type`, `get_resolution_list`, `get_dpi_list`, `open_system_screen_setting_page`, auto/AI brightness support calls |
| Touchpad | `get/set_touchpad_vibration_intensity({mode})`, `get/set_touchpad_pressing_sensitivity`, `get/set_touchpad_edge_sliding`, `get/set_touchpad_gestures_screenshot`, `get/set_haptic_feedback({on_off,is_screenshot})` |
| Touchscreen | `get_screen_touch`, `set_screen_touch`, `register_screen_touch_btn` |
| Drivers | `scan_drivers` (persistent), `get_drivers_detail`, `install_driver({hardware_id})`, `repair_driver({hardware_ids,repair_all})` (persistent), cancel calls, `get_driver_version`, `popup_driver_manager` |
| Cleaner | `scan_rubbish` (persistent), `get_rubbish_detail`, `stop_scan_rubbish`, `stop_clean_rubbish`; native cleanup owns file deletion |
| Startup optimization | `scan_startup`, `get_startup_items`; OEM owns decisions on services/tasks/startup |
| Store | `get_app_store_state`, `open_app_store` |
| Device info | `get_pc_basic_info`, `get_cpu_basic_info`, `get_gpu_basic_info`, `get_physical_memory_info`, `get_monitor_info`, `get_network_adapter`, `get_baseboard_info`, `get_operating_system_info` |
| Sharing | MiDrop, remote control/file manager, clipboard, application relay and screen sharing methods |
| Search | `get/set_ai_search_switch_btn`, `get/set_ai_index_path_option`, `get/set_timing_of_create_index_option`, `get/set_ai_search_short_cut_option` |

`native-methods.csv` is an automatically extracted inventory of direct `L`/`R` calls in the supplied main.js, with offsets for reproducible lookup. It is a list of observed call sites, not a verified public API contract.

XiControl's separate HTTP API uses Bearer authorization, default loopback port 58125, opt-in command permissions, and admin-protected API settings. Implemented routes are exactly `GET /status`, `POST /mode {value}`, `/care {on}`, `/travel {on}`, `/owl {on}`. Status fields are mode/care/travel/owl/batteryPercent/charging/watts/health. It does not expose an arbitrary charge threshold, touchpad, refresh-rate, brightness or full telemetry API. Its actions are queued onto the UI thread in TrayApp, so `{ok:true}` is acceptance, not proof of applied hardware state.

## 6. Hardware control and setting map

This covers observed setting families, including settings outside the main hardware pages. Unknown or proprietary internals are explicitly retained with Xiaomi.

| Setting/capability | Xiaomi | XiControl | Windows | Recommended owner |
| --- | --- | --- | --- | --- |
| Performance | workload bridge | MifsClient + dialect/readback | OS power policy is a different control | Xi-derived firmware, preserve actual supported modes |
| AC/battery performance defaults | OEM behavior | PowerProfileGuard | Power-source events | Shared XiControl power-profile guard and firmware writes |
| Charge care/40–80% threshold | charging bridge | MifsClient + ChargeGuard | No generic Windows charge-limit API | Xi-derived firmware + guard |
| One-off travel charge | OEM full charging | travel state/monitor | Battery/power events | Our state + Xi-derived writes, restore after unplug |
| Battery health/cycles/capacity | battery bridge | BatteryInfo | ACPI battery WMI | Windows through Xi-derived reader |
| Adapter power | OEM info | GetAdapterWatts | Not generic negotiated PD telemetry | Xi-derived, distinguish adapter rating from consumption |
| Brightness | brightness bridge | Brightness + WmiQuery | WmiMonitorBrightness | Windows through Xi-derived reader/writer |
| AC/battery brightness memory | OEM behavior | Brightness/guards | Power events | Our explicit opt-in policy |
| Brightness cap/learned ALS curve | Distinct from content-aware AI brightness | BrightnessCapGuard/AutoBrightnessGuard/ALS | Sensor/power APIs | Connected native advanced settings and original guards; hardware validation pending |
| Content-aware AI screen brightness | OEM Display settings and model/service | No equivalent supplied implementation | No equivalent content-aware toggle | Original Settings > Display forwarding |
| Refresh rate/automatic AC-DC | screen ratio bridge | RefreshRate + guards | CCD/ChangeDisplaySettingsEx | Xi-derived Windows implementation targets internal panel |
| Resolution/DPI/HDR | OEM bridge | Not a complete replacement | Windows Display settings | Windows forwarding |
| Gamut/ICC/eye care/OLED protection | OEM bridge/profiles | Incomplete coverage | Color management partially available | Xiaomi forwarding until reproduced precisely |
| Touchpad/touchscreen enabled | touch/input bridge | HidNodeToggle | SetupAPI/ConfigManager | Xi-derived, with disable recovery |
| Haptic strength/click force | touchpad bridge | TouchpadHaptics + protocol | HID I/O transport | Xi-derived BLTP7853 descriptor-gated implementation |
| Edge sliders/dead zone/heavy press | OEM gesture bridge | RawTouchpadReader/gesture modules | Raw input/registry/HID | Connected native advanced settings and original gesture/HID implementation |
| Mi/AI/settings/projection keys | function-key bridge | KeyMap/KeyRouter/event watcher | Hotkey APIs | One opted-in key owner; avoid duplicate actions |
| Stay awake | OEM/system settings | AwakeMode includes lid-policy override | SetThreadExecutionState | Idle-sleep prevention by default; optional display-awake and AC lid override with recovery |
| CPU/GPU/RAM | hardware info | CpuLoad/GpuTelemetry/MemoryLoad | GetSystemTimes/IGCL/GlobalMemoryStatusEx | Xi-derived telemetry |
| Power | OEM telemetry | Battery IOCTL and RAPL energy meter | ACPI/energy meter | Distinct battery-flow, CPU-package and GPU labels |
| Temperature/fan/NPU | device-specific | DPTF/ACPI zones; no complete NPU/fan reader | Driver/provider specific | Temperature with source label; NPU/fan unavailable until valid provider exists |
| Camera/microphone/audio/network | OEM settings | MicControl and limited keys | Windows settings/media APIs | Windows forwarding; OEM camera processing retained |
| Drivers/BIOS/firmware | OEM updater | No equivalent update engine | Windows Update partial coverage | Xiaomi performs OEM updates, Windows performs Windows Update |
| Cleaner/optimization/app repair | OEM plugins | Not applicable | Partial system tools | OEM launch, never bypass review/delete directly |
| Startup/appearance/language | OEM config | AutoStart/Loc/UI themes | Task Scheduler | Our separate config, English, light/dark, optional startup |
| Notifications/OSD/tray | OEM OSD | OSD/tray forms | NotifyIcon | Native OSD and tray metrics connected to shared settings and events |
| Desktop wallpaper/screensaver/weather | personalize bridge | Not core | Windows personalization | Windows/OEM forwarding, low priority |
| Xiaomi interconnect/clipboard/relay/remote files/casting | proprietary services | Not provided | Limited nearby-sharing alternatives | Excluded by the latest user request |
| AI search/index path/timing/hotkey | proprietary search bridge | Not provided | No semantic index by default | New local SearchService in later project phase |
| Offline translation/model/runtime settings | proprietary AI tools | Not provided | Capture/OCR building blocks only | New local TranslationService in later phase |
| OEM account/privacy/feedback/support/store | OEM backend | Not provided | Not a replacement | OEM forwarding, no duplicated accounts |
| HTTP API/webhooks/smart-plug reminders | Not established | ApiRouter/Webhook/ChargeLimitWatcher | Networking only | Connected opt-in advanced API/webhook settings; not used by the embedded webview |

Mode names are not interchangeable numeric values across Xiaomi and XiControl. Active AC choices are Silent=Quiet(2), Smart=Auto(9), Full speed(4), Eco(10). Active battery choices are Eco(10), Quiet(2), Auto(9), Turbo(3). Balance=1 is retained only for legacy readback, never offered or written by active controls. Old invalid per-source profiles are cleared without inventing a replacement. Xiaomi's JS enum differs. Support is confirmed through readback after intentional selections, not automatic write probes.

## 7. Proposed project structure

```text
XiaomiPCManager/
  5.8.1.121/                 existing English OEM reference, unchanged
  XiControl-0.16.0/          existing upstream source, unchanged
  XiaomiAIManager/
    XiaomiAIManager.csproj
    Program.cs
    ManagerApplication.cs    shared native services, tray and policy lifetime
    MainWindow.cs            compact/secondary forms and trusted WebView2 messages
    DesktopShortcuts.cs      fixed Windows shortcut allowlist
    Services/                router, hardware, telemetry, OEM launch, preferences
    Vendor/XiControl/        reused backend, guards, complete advanced settings + LICENSE
    www/quick.*              primary white popup, controls operated inline
    www/index.html           secondary manager, app.js and style.css
    www/bridge.js            shared native request/reply handling
    docs/                    architecture, method inventory, validation guide
    README.md
```

Use .NET 8 WinForms for native window/tray lifetime and WebView2 hosting. Plain HTML/CSS/JS implements the primary popup and six secondary pages: Home, Device, Performance, Toolbox, More settings and Settings. All UI calls use a fixed capability router. No raw firmware opcodes, shell strings, arbitrary launch paths or OEM bridge method names are accepted from the webview. The executable-selection action uses a native user-operated picker.

## 8. Capability-to-backend contract

`UI → validated capability router → serialized service operation → Xi-derived hardware / Windows / OEM launch → applied state or English error → UI refresh`.

Reads return nullable measurements with timestamp/source. Writes validate values, execute sequentially off the UI thread, and read back where possible. Failure never becomes a simulated success. Capabilities absent on the machine are unavailable, not zero. The webview is loaded from a local virtual host; navigation, new windows and privileged message origins are restricted. Follow Microsoft's [WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security) and [local virtual-host API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping).

## 9. Reuse

The complete XiControl command layer, configuration, policy guards, key routing, advanced settings pages, OSD, monitor, and optional HTTP API are now reused in the same resident. Hardware transport and haptics are shared with the Xiaomi popup. The original XiControl executable, TrayApp, and dark QuickPanelForm are not used. English resources and SVG/WAV assets are embedded, with the original Svg dependency declared. See SETTINGS_COVERAGE.md for setting-by-setting handlers and evidence limits.

## 10. Rewrite

Our DOM, navigation, state, English copy, cards/toggles/segmented controls, error/status handling, app preferences, telemetry presentation, and policy ownership. Separate hardware work from UI notifications. A standalone app should not load Xiaomi's giant bundle or reference its Redux state.

## 11. Launch or forward

OEM drivers/firmware, cleanup, optimization, store, proprietary display/camera/gesture settings, and support. Sharing/interconnect are excluded. Windows display/camera/audio/network/update settings use an explicit allowlist of `ms-settings:` destinations. Driver scan uses --scan_driver. Toolbox/settings use scoped visible-label navigation and report failures. No guessed deep link or raw IPC payload is sent.

## 12. Technical risks and evidence limits

1. Firmware/HID commands may be unsupported even on similarly named laptops. Runtime reads and write readback are mandatory.
2. Xiaomi and XiControl can compete with our charge/profile/brightness rules and key handlers. Remembered AC/battery performance is enabled as requested; other automatic policies are opt-in. Conflicting OEM policies still require avoiding competing rule owners.
3. The elevated native process must reject untrusted webview messages. The UI must not gain arbitrary execution or raw firmware access.
4. Driver scan has a static parser/handler contract. Other IPC schemas remain unverified; string presence alone is insufficient. Accessibility invocation and final-page arrival require runtime checks.
5. GPU IGCL uses a driver ABI layout verified by XiControl on TM2424, not every Intel machine. Initial counter samples and unavailable measurements remain null.
6. RAPL package power is not whole-laptop power. DPTF hotspot and ACPI thermal-zone temperature are different measurements. NPU load and fan speed must not be invented.
7. English JS does not translate native OEM windows or image text. Our own product is English; external OEM language is outside this patch.
8. Charge protection needs resume/power/session handling. Travel charging needs state recovery, not just a 100% toggle.
9. Persistent input disable requires recovery, even after interruption. Track only nodes disabled by our app and avoid controller nodes.
10. Semantic search/translation need local models, a measured acceleration pipeline, indexing policy and offline validation. Navigation placeholders cannot count as implementation.
11. `dotnet --list-sdks` returned no installed SDK in this environment. Build/package execution requires user-provided tooling; no package installation or runtime tests were authorized in this task.

## 13. Implementation sequence

1. Record this inspection and preserve the supplied English patch as the visual reference.
2. Implement the compact white popup as the primary English interface, with controls changed directly inside it; keep the bigger window secondary.
3. Integrate reusable hardware controls and honest telemetry; add shared tray lifetime and explicit automatic-policy ownership. Expose only implemented control pages and OEM forwarding.
4. Statically validate source, route/message consistency, XML and vendor dependency closure. Provide user-run build/hardware checks without executing them.
5. Validate the compiled application on the actual laptop, including refused modes, sleep/resume, charger changes and input recovery.
6. Investigate precise OEM launch arguments/IPC with a verified parser contract, then upgrade forwarding.
7. Add independent semantic-search and offline screen-translation modules using the larger project requirements, with real model/runtime availability states.

The implementation is a compiled hardware-manager test foundation. Compilation and static contracts do not establish hardware compatibility and do not complete the later AI modules. The local test build has separate data and numbered original Xiaomi performance cards. It reuses XiControl's monitor UI and policies; proprietary OEM settings continue to forward to Xiaomi PC Manager.


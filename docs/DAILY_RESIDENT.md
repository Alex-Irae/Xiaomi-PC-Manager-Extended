# Daily resident and measured validation

**2026-09-28 update:** the manager is centered, frameless and always closes to tray. The popup defaults to 90% scale with compact spacing, Home/Refresh in the header and no bottom footer. Profile pictures, color presets, custom colors, custom shortcuts and stock OSD presets are available. Manual display rates are retained until a real power-source transition. The logon task preloads the popup with zero trigger delay; a per-user UI cannot appear before interactive sign-in. See [feedback fixes](FEEDBACK_FIXES.md) and [runtime evidence](RUNTIME_TEST_REPORT.md) for verification and remaining physical checks.

**Current evidence:** [RUNTIME_TEST_REPORT.md](RUNTIME_TEST_REPORT.md) supersedes the early measurement conclusions below. The daily popup is preloaded, firmware keys enabled, own Settings categorized, and explicit WebView suspension removed. Startup is registered and demand-start tested; reboot and long-term stability remain unverified. Mi opens the popup, Settings/F9 opens Windows Settings, and Keyboard offers native app selection and manager pages.

The daily build is `app/XiaomiAIManager.exe`. It is running as the current user's elevated tray resident, with a confirmed per-user Windows logon task. Use **Open daily manager.cmd** for the popup. Closing the popup keeps the process running. No custom hotkeys are installed by default. Keyboard provides editable Ctrl/Alt/Shift/Win chords. AI/F7 now resolves the newest installed XiaoAI; Fn+K remembers firmware changes and Fn+S cycles rates.

## Changes and their purpose

`Mouse / fixed launcher command -> resident -> direct XiControl/Windows handler -> device readback -> local UI or lower-center Xiaomi OSD`

Performance OSDs use the original English light-theme Xiaomi cards, displayed at the lower center of the current screen's working area. An opaque neutral background and rounded native region replace magenta color-key transparency. Test mode retains numbered badges. The Notifications position setting now applies to both performance and other notices. Lower center is the default, not a forced placement.

Brightness writes request `Timeout=0`, reuse the discovered WMI method targets, and bypass the diagnostics/OEM capability queue. Slider input is applied during dragging. One write runs at a time per slider, with only the latest pending value kept. The host verifies the level through readback. This laptop's provider returns no status value, so a void reply is accepted before readback. The cache is cleared after resume or a failed invocation.

The compact popup reads hardware without collecting full CPU/GPU telemetry or repeatedly locating the OEM executable. Battery health, haptic state, PnP state and supported rates are cached for 60 seconds and invalidated after relevant changes. The popup preloads at startup; hidden frontend refreshes are skipped. Explicit WebView suspend/resume was removed because it broke reopen. Configuration notifications replace the former one-second reconciliation timer. ALS polling/streaming runs only while automatic brightness or the relevant settings controls needs it. Power events apply policies immediately, with a 30-second source-check fallback.

Charging callbacks validate current intent under the shared firmware lock. A stale care limit cannot replace a later full/travel request. Native and web travel cancellation clear the same recovery target. Startup clears travel and restores its saved care limit; ordinary persistent full charging remains full. During the same session, current intent is reconciled on resume and source changes. Failed travel release retains recovery data. The UI reports a mismatch between requested and actual firmware limits.

Within this resident, only explicit **Driver updates**, **Xiaomi tools**, **Open Xiaomi Manager** and **Xiaomi store** actions launch the OEM UI. These actions re-enable MiDeviceService on demand when daily isolation is enabled. Proprietary settings such as OEM content-aware brightness and color profiles still require the explicit Open Xiaomi Manager button. They are not silently applied or claimed as standalone controls. No XiControl executable is called by the resident. Kernel firmware interfaces remain available.

**OEM watchdog isolated:** after logon, the installed OEM `OSDUtility.exe` launched `XiaomiPcManager.exe --open_controlcenter`. The installed MiDeviceServiceEntry module and active log identified the watchdog supervising OSDLauncher, including touch-screen events. Run/task changes alone did not cover that path. After the user approved reversible service isolation, MiDeviceService was stopped and set to Disabled; the verified OEM UI, host and OSD processes in this session were stopped. Other OEM services and kernel drivers were left unchanged. Current process/service checks confirm the requested isolated state; a reboot and broad device compatibility remain untested. A speculative singleton suppression was rejected after its privileged lookup failed.

## Startup changes and recovery

The installation preserved and disabled:

- The Xiaomi UI Run entry: `Launch.exe --AutoRun=1`, saved in `.test-environment/deployments/20260927T050015818/oem-run-entry.json`.
- `XiaomiPCHostTask`, with original XML saved in the same deployment folder.
- `XiControl_<USER_SID>`, with original XML saved in the same deployment folder.

The verified Xiaomi UI/host processes and previous project test resident were stopped. Their source/install files were not deleted. This app's task is `XiaomiAIManager_<USER_SID>`, targeting the daily executable with `--tray`. It permits battery operation, has no run-time limit and allows three failure restarts at one-minute intervals. `startup-task.xml` remains in the app's local data directory. Failure restart and the optional one-minute recurrence are different mechanisms. With Restart automatically enabled, recurrence also recovers clean exits. Disable that option before an intentional lasting exit. Sign-in and console-connect triggers have zero configured delay.

The resident was absent at the later afternoon check, with task result 0. The cause of that clean exit is unknown. A demand restart was confirmed responding for more than ten minutes; an immediate three-second startup check was too early. Lifecycle logging now records Start, Ready, the quit reason, message-loop completion and disposal. A pending WebView2 suspension was initially guarded during shutdown; the later lifecycle fix removed explicit suspension entirely. Neither change proves long-term stability or establishes the earlier exit cause.

To restore an OEM startup entry, read the saved JSON and recreate only that exact registry property in an administrator PowerShell. The scheduled tasks still exist and can be re-enabled by their exact names with `Enable-ScheduledTask`; reimport is unnecessary. Do not restore XiControl startup if it has been uninstalled.

Double-click **Restore Xiaomi service.cmd** to restore MiDeviceService's saved startup mode and running state. This was exercised successfully: Automatic/running, followed by a successful return to Disabled/stopped using **Disable Xiaomi service.cmd**. Those launchers request administrator access and leave logs in `app/service-state/CURRENT_USER_SID`. `oem-service-original.json` preserves the original executable/startup/running state; `oem-service-permissions.sddl` preserves the original service permissions. A changed executable or unknown permissions causes refusal instead of a guessed restore. The controller can recover its exact temporary ACL after an interrupted operation. Restoring this service does not re-enable the other saved startup entries. For returning to OEM daily control, exit this resident first to avoid competing policies.

Initial service records in the local application-data directory were not visible to the real scheduled resident, despite being visible to the tool-launched helper. The known-good original state/permissions were copied, without overwriting an existing backup, into the shared workspace beside the deployed app. The previous records remain preserved. Both the resident and source/packaged service scripts now use the same deployment directory and user SID. The final resident log confirms `OEM service action: Disable` before hardware initialization and `Resident.Ready`. Keep `app/service-state` with the executable and preserve it during updates.

The OEM service explicitly denied CHANGE_CONFIG and STOP to Everyone/System, including administrators. The controller temporarily removes only those deny bits, performs its requested operation and restores the exact saved SDDL in `finally`. Both actual disable and restore checks confirmed the original permissions afterwards. An initial access-denied attempt is preserved in its action log. No permanent permissions relaxation or OEM binary patch was made.

Explicit OEM tools set the service to Manual and start it. After its driver/firmware work finishes, use **Disable Xiaomi service.cmd** to return to isolation, or exit and restart our resident. Automatic cleanup now defaults on after an explicit OEM manager session. It requires a detected visible landscape main window, then waits six seconds after that window closes or hides. MSI and matching/uncertain installer processes delay cleanup. Settings can turn this behavior off. The watcher exists only during an explicit OEM session and never kills installers. Do not use the Disable launcher or restart the resident during an OEM installation. No idle service/process polling was added. The on-demand Manual/running path was exercised successfully; final driver-page navigation remains unverified. A new resident startup reapplies requested isolation independently of basic hardware initialization. Restore Xiaomi service.cmd clears that intent, so later resident launches respect the manual restoration.

The Disable launcher sends fixed `--reapply-policies` to an existing resident after stopping the OEM service. This restores current saved charging/display/performance intent without changing saved selections or starting another resident. Startup waits for initialization before servicing that notification. Temporary OEM restoration reapplied its own 60% charge target: run 011 preserved that actual/requested mismatch after service-only cleanup. The corrected restore/disable cycle in run 012 confirmed actual/requested 80% again. Service operations compare the full saved SDDL against privileged readback. A non-elevated `sc sdshow` omits the audit section and cannot be compared textually with the elevated backup; its DACL was separately confirmed equal.

The daily and isolated test builds now share a hardware-owner mutex. Exit the daily resident before launching the test app. Test launchers use the current `app` build when available, with separate `app/test-data` settings and numbered OSDs. The earlier test settings remain preserved under `.test-environment/artifacts/bin/XiaomiAIManager/release/test-data`.

## Earlier results and interpretation, superseded by the runtime report

| Run | Condition | Mean CPU, all descendants | Mean summed working sets | Whole CPU package, other workloads included |
| --- | --- | ---: | ---: | ---: |
| 001 | Previous test resident, visibility uncontrolled | 0.0808% | 724.4 MiB | 3.11 W |
| 002 | Revised resident, before any web window opened | 0.0017% | 72.1 MiB | 5.53 W |
| 003 | Revised resident, popup opened then hidden | 0.0237% | 492.4 MiB | 6.66 W |
| 006 | Revised resident, popup opened then hidden | 0.0711% | 488.5 MiB | 5.75 W |
| 007 | Charging-intent safeguard included, popup opened then hidden | 0.0682% | 478.6 MiB | 4.15 W |
| 010 | OEM service disabled; popup opened then hidden; battery power | 0.1066% | 504.3 MiB | 3.07 W |

Each observation is about one minute. These are not paired controlled before/after runs: other desktop activity, builds, charging, the observer and initialization differ. The final result is low CPU usage, with appreciable WebView2 memory retained after opening. It does not prove long-term stability or a particular battery-life cost. The variation between 003 and 006 prevents claiming a precise CPU improvement from these samples. Whole-package watts increased in later samples; that is inconclusive about this app because other workloads were not controlled. App-only watts remain unavailable.

[Early measured summary](../results/010_20260927T134834098_resident/summary.json) and [four-panel figure](../results/010_20260927T134834098_resident/resident-figure.svg). Run 010 includes lifecycle logs, shutdown protection and service integration. These historical samples predate the current session watcher and recurring restart trigger; use the latest runtime report for current measurements. Assembly hashes are preserved in measurement configs.

Hardware run 005 used the same production handlers as the UI:

- Same-value brightness at 51%: write approximately 7.6 ms, verified round trip approximately 12 ms.
- Full charging: firmware reported 100% after two seconds.
- Travel charging: firmware reported 100%, recovery target 80%.
- Cancelling travel and final cleanup: firmware reported 80%; saved care intent restored.

[Hardware evidence](../results/005_20260927T051029960_hardware/summary.json). Run 004 is preserved as a failed probe: the initial void-return brightness check rejected the provider and prevented charging checks. That defect was corrected before 005. Firmware readback does not prove the physical charging cutoff over a full charging cycle. Charger unplug/replug, sleep/resume and long-run stability remain untested in this session. The brightness result is a same-value backend timing, not an optical response or end-to-end UI latency measurement. Multi-monitor/DPI visual inspection remains manual.

With MiDeviceService stopped, run 008 confirmed a 17 ms brightness round trip but skipped charging because the laptop was on battery. Run 009 added a complete read-only hardware snapshot: MIFS available, charge limit/request both 80%, battery modes Eco/Quiet/Auto/Turbo, brightness 51%, available rates 60/120 Hz, input nodes enabled, and haptic strength/pressure available. Same-value brightness took about 13 ms. Full/travel charging was again skipped on battery. Earlier AC charging proof in 005 predates service isolation, so the combined service-off/full-charge condition remains unverified. The current BIOS/model and source are preserved in [009 hardware results](../results/009_20260927T134826559_hardware/summary.json).

[Final hardware readback](../results/012_20260927T141509717_hardware/summary.json) follows the actual restore/disable/notification cycle: firmware reported the saved 80% limit, MIFS remained available, and brightness write/readback took about 16 ms. Its config records service startup 4 (Disabled) and isolationRequested true. Full/travel probing was skipped on battery. Same-value round trips across these checks ranged approximately 13–53 ms; this does not establish an optical or UI latency bound. Repeated restore/disable actions preserved original service state and permissions. These explicit-action corrections were compiled/deployed after the idle sample without repeating idle measurement.

## Algorithms, parameters and figure axes

CPU percent is $100\sum_i\Delta C_i/(\Delta t\,N)$, where $C_i$ is each process's CPU seconds, $\Delta t$ is wall time and $N$ is the logical processor count. Lower is better for a hidden idle app; no universal threshold certifies battery impact. The first sample has no CPU delta and is excluded from summaries. PID/start-time mismatch or resident exit invalidates a run. New descendant processes have no CPU delta until their second observation, which can undercount short-lived children.

Working sets sum reported resident pages across the app and its descendant WebView2 processes, divided by $2^{20}$ to obtain MiB. Lower suggests less page residency, but shared pages can be counted repeatedly. This is not unique physical RAM or private committed memory. CPU package power divides the RAPL/Windows Energy Meter milliwatt counter by 1000. It describes the whole processor package and is not attributable to the resident. Battery watts divide reported charge/discharge mW by 1000; charging is positive and discharge negative. Missing counters remain blank, not zero. Plugged-in battery charging data cannot establish battery runtime.

The four figure panels have elapsed seconds on the horizontal axis and process-tree CPU %, summed working-set MiB, whole CPU package W and signed battery flow W vertically. Low, stable CPU after hiding is the desired idle behavior; spikes or continued sustained load indicate more investigation. Working-set retention after opening is observed with retained WebView2 processes. Power spikes can belong to any system workload, so no plot panel establishes app-only watts. Each figure uses its own vertical scale and must not be compared by line height alone.

Sampling interval is nominally three seconds, with actual elapsed times recorded; CIM overhead makes it somewhat longer. Default duration is 60 seconds. `config.json`, `samples.csv`, `summary.json` and a standalone SVG figure live in new numbered results folders. Seed is null because observations do not use randomness. No previous result folder is replaced. Future runs also record the daily assembly SHA-256. No additional plotting dependency was installed; the figure exporter uses Python's standard library.

## Files, environment and commands

Daily settings and logs are `%LOCALAPPDATA%/XiaomiAIManager`. The existing .NET 8 Desktop runtime and WebView2 are used. Build packages/SDK stay project-local; the daily executable path should remain stable.

Fixed commands suitable for user-chosen shortcut bindings are `--toggle`, `--manager`, `--cycle-mode`, `--brightness-up`, `--brightness-down` and `--toggle-travel`. Brightness steps are five percentage points. `--hide` dismisses web windows; `--quit` exits cleanly. Monitor commands remain `--monitor-small/medium/large`. Nothing binds Ctrl+Alt+numbers or changes modes merely to preview the OSD. All hardware commands use the resident's direct backend.

Build into staging while the resident runs:

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.test-environment/cli-home"
$env:NUGET_PACKAGES = "$PWD/.test-environment/packages"
& ./.test-environment/dotnet/dotnet.exe build XiaomiAIManager.csproj -c Release --artifacts-path .test-environment/artifacts --no-restore -o .test-environment/daily-next
```

`Install daily resident.cmd` preserves competing UI startup entries and installs or updates the staged daily build, with administrator approval. For an existing task, use `powershell -NoProfile -ExecutionPolicy Bypass -File tools/update-resident.ps1`; it verifies the path, clean exit, copy and single-resident restart. `Open daily manager.cmd` activates the running popup. Do not copy over a running executable.

Measurements:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/measure-resident.ps1 -ResidentId ACTUAL_MANAGER_PID -Seconds 60 -Label hidden
python tools/plot-resident.py results/NEW_MEASUREMENT_FOLDER
```

Hardware verification is opt-in: `app/XiaomiAIManager.exe --verify-hardware`. It requires AC, travel off and a configured care limit. It briefly changes charging to full/travel and restores the saved limit in `finally`. It does not change the performance mode. A restoration failure is saved explicitly. The probe must not be treated as validation of a physical charge cutoff.

## Current restart and recovery behavior

Startup defaults on with an additional recurring one-minute check. The task ignores new instances while its existing resident is running. A clean Exit therefore restarts within approximately one minute unless automatic restart is disabled in Settings. Failure recovery also retains three one-minute retries. The updater pauses recurrence before copying and restores task enablement. No separate resident watchdog consumes idle CPU.

Input switches no longer display confirmation dialogs. Owned soft and persistent disables are both tracked. Each input's keep-disabled-after-restart toggle defaults off; startup recovery enables owned disabled devices. When enabled, a soft node revived by a Windows reboot is disabled again. This does not override devices disabled independently by another application or Device Manager.

## Popup and screen-off behavior

Battery care is a persistent toggle. Its remembered care percentage is set in the manager; disabling care persistently allows 100% charging. Travel is a separate temporary exception and is not resumed at startup. Both routes share the original XiControl charging transport.

Screen off now sends a direct Windows display-power request after the initiating click or key is released. It does not alter sign-in, display-timeout or sleep rules, so Windows may lock. The earlier no-lock workaround caused severe wake flickering and remains retired. Startup and exit retain its recovery path for `%LOCALAPPDATA%\XiaomiAIManager\manual-screen-off.json`; the inspected journal was inactive. The replacement has not been physically retested. Stay awake is separate and can prevent idle sleep; closing a window to the tray does not enable it.

The resident starts the one-time Windows hardware inventory scan ten seconds after basic controls become ready. The manager keeps its last complete inventory through quick reads and stores a bounded last-known UI snapshot in WebView storage. On renderer restart it shows that snapshot with an updating label until a live read succeeds. Stored values are a display cache and must not be treated as current firmware confirmation.

The popup requests foreground activation on opening and retains visibility through device changes. Outside mouse clicks dismiss it through a hook active only while the popup is visible; a tray click already closing it does not reopen it. Placement uses screen bounds, so the bottom-right panel covers the taskbar clock and stays fixed when auto-hide changes. The native window fades over about 220 ms and can be disabled in Settings; Windows' reduced-motion preference is respected. The scrollbar is hidden while wheel scrolling remains available. The popup has a four-control display row and a separate hardware row. Firmware writes run on a worker, with rapid clicks serialized and the relevant pending control disabled until confirmed.

TM2424 does not expose a confirmed keyboard-backlight setting through XiControl MIFS or standard Windows HID, so Screen off cannot currently force that light off. Fn+K is not delivered by its firmware WMI key channel; Fn+S emits ordinary S. Use the Keyboard page's custom Windows shortcuts for alternate bindings.

For repeatable 60-second observations of the hidden resident, visible popup, three monitor sizes and the full manager, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-popup-resources.ps1 -Seconds 60
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-resources.ps1 -Seconds 60
powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-manager-resources.ps1 -Seconds 60
```

The observer does not change hardware settings. It finishes hidden in the tray and writes fresh numbered protocol/sample folders. The visible condition includes normal frontend refresh and the outside-click hook, but does not repeatedly trigger animations. Whole-machine watts cannot establish animation cost or app-only power draw.

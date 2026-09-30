# Validation and acceptance

Current results and their limits are in [RUNTIME_TEST_REPORT.md](RUNTIME_TEST_REPORT.md). All runtime checks in this session were explicitly authorized by the user. Earlier failures remain in their original result folders and are not relabeled as passes.

## Automated checks

From the XiaomiAIManager directory, use the existing project-local SDK and packages. No environment creation, installation or restore is needed for this prepared checkout.

```powershell
node .\tools\check-ui.mjs
node .\tools\check-behavior.mjs
& ./.test-environment/dotnet/dotnet.exe ./.test-environment/daily-next/XiaomiAIManager.dll --check-settings
```

The first command checks syntax, assets and native contracts. The second executes production frontend handlers against an in-memory fixture, including slider coalescing, mode selection, charge estimation and native key-app selection dispatch. The last validates native bounds, choices, key maps, curves, OSD positions, page/app routing and unassigned/unknown app refusal. None initializes hardware or launches an app.

## Authorized live checks

Only run these after authorizing the relevant changes:

```powershell
.\app\XiaomiAIManager.exe --validate-ui
.\app\XiaomiAIManager.exe --validate-controls
.\app\XiaomiAIManager.exe --validate-oem
```

UI checks open/read/hide the warm popup, read the catalog, change then restore notification preferences, preview native OSDs and reject invalid requests. They do not change performance, brightness, charge or input hardware settings. Control checks briefly change those hardware settings and restore the native baseline in finally. Have another input device available. OEM checks explicitly open original software and restore isolation, but never install a driver.

The running scheduled resident must receive the request. In this tool session an unelevated helper could not find its window; an administrator launcher succeeded. A command that returns or opens a helper is not evidence that validation ran. Confirm a new summary with the actual assembly hash, completion and restoration fields.

Results use fresh numbered results/NNN_timestamp folders with configuration, assembly hash, baseline, raw request timings, readbacks, errors and restoration evidence. Never overwrite an old run. An API echo alone does not prove a physical result; a fast return is not optical latency.

## Physical acceptance still needed

- Reboot/sign-in: one resident, prompt popup, OEM service/process isolation and functioning Mi/F9 mappings. Check sleep/resume separately.
- Plug/unplug: independent remembered AC Silent/Smart/Full speed/Eco and battery Eco/Quiet/Auto/Turbo; no Balanced choice.
- Charging: direct driver flow above a care target and full/travel was tested in run 023. Still check natural threshold crossing and an entire charging cycle on the current build. Cancel travel must restore its saved target.
- Keyboard: user confirmed current Mi/AI/F9/Projection responses. Select a different app in Keyboard, cancel once to check mapping preservation, then select its executable and verify normal-user launch. Test Mi double/hold separately and check for duplicate OEM actions.
- Notifications: native top/bottom placement and neutral performance background passed. Check physical Caps/Fn/Num and performance events visually on each relevant screen/DPI.
- Input/display: each supported gesture, ALS curve, haptic setting and display-rate transition requires physical observation; unsupported sensors must stay unavailable.
- Customization: check popup position/offset clamping, icon PNG selection, app labels/order and launch. The picker supports executable files, not arbitrary Store app identities.
- OEM tools: exact requested page and completed driver scan remain unconfirmed; Toolbox/Home/Store navigation failed. Return to isolation only after any installer finishes.
- Optional API/LAN/firewall/webhooks remain off and untested. Enable only for a deliberate test.
- Resource behavior: perform a longer idle/active observation with controlled workloads. Short process-tree CPU and whole-package watts do not establish app-only power or battery life.

For manual results, use the next numbered folder and save config.json with executable hash, device/BIOS, OS, source, screen/DPI, baseline policies, exact protocol and checks attempted. Save passes, failures, refusals and untested cases separately, alongside useful screenshots and logs. Preserve originals and failed results.

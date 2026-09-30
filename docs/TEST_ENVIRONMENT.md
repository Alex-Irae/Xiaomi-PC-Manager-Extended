# Local test build

Current behavior and measured evidence are in [RUNTIME_TEST_REPORT.md](RUNTIME_TEST_REPORT.md). Test launchers use the deployed `app` build when available; exit the daily resident before starting the separate test identity. Normal daily startup enables independent firmware keys; test mode disables them. No new environment/package installation is required on this prepared workspace.

The ready Release build is under `.test-environment/artifacts/bin/XiaomiAIManager/release/`. Double-click **Test Xiaomi Manager.cmd** in the project root. Accept the Windows administrator prompt to access Xiaomi firmware. The popup says **Quick controls · Test**. The first launch initializes WebView2 and native hardware readers.

Other launchers open All controls or the Small, Medium and Large monitor views. They activate the same test resident. The tray menu also offers the three sizes. No keyboard shortcut is added.

## Mouse performance and numbered OSD

| Number | AC | Battery |
| --- | --- | --- |
| 1 | Silent | Eco |
| 2 | Smart | Quiet |
| 3 | Full speed | Auto |
| 4 | Eco | Turbo |

Click a mode in the popup. The host validates the displayed power source, calls the imported XiControl MIFS implementation and reads the actual mode back. Only a confirmed selection gets saved and shows our Xiaomi card with a blue numbered badge at the lower center. A refusal reports an error. Xiaomi's own running OSD remains enabled and may appear separately. The OEM card artwork retains its original English text, including Smart, Silent Mode and Endurance.

Each source remembers its own successful selection. Select a mode while plugged in, unplug and select a different battery mode, then reconnect and unplug again. The app should restore the corresponding remembered mode. A never-configured source stays unchanged. Balanced is not offered. Test launch does not install Ctrl/Alt/number shortcuts.

To inspect artwork without touching modes, open All controls > Notifications and use Performance preview or Caps Lock preview. These preview actions do not read or write the firmware mode. They use the current power source solely to choose the corresponding card.

## Monitor interpretation

The monitor reuses the supplied XiControl layout, colors, graphs, dragging, view cycling and metric menu.

- Small: one large metric, default battery watts. Right-click to select a supported metric; double-click to change view. Escape hides it.
- Medium: battery watts, CPU/GPU utilization and RAM in a strip. GPU appears only with a reporting Intel IGCL source.
- Large: graphs, battery flow, adapter rating, CPU utilization and separate CPU package watts, GPU utilization/MHz/watts, RAM percent/GiB and a reported temperature source.

Sampling runs at 1 Hz while visible, retaining up to three minutes. Counter-based readings need two samples. Battery watts are signed net flow into/out of the battery, not wall consumption. Zero flow is currently displayed as unavailable by the imported reader. CPU package power comes from Windows Energy Meter's RAPL package counter and excludes the rest of the laptop. GPU watts depend on its driver. Adapter watts are the connected adapter's reported rating. NPU load, fan RPM and wall power have no implemented sensor source. Do not sum these partly overlapping measurements as a total.

## Isolation and files

Test settings, WebView2 data and logs live beside the test executable in `test-data/`. The test resident has a distinct mutex and window title. It does not read normal app settings, register startup, subscribe to firmware keyboard events, start the HTTP API, alter API firewall rules or send webhooks. Fresh settings leave performance/brightness/refresh/charging policies disabled. Mouse selection enables performance profile memory; intentional device settings persist across subsequent test launches.

This is software-data isolation, not simulated hardware. Firmware, display and input controls operate on the same physical laptop as Xiaomi PC Manager. A second manager's automatic policy may override a selection. Preserve the desired baseline settings when testing. Exit from the test tray menu to stop its resident policies; dismissing a window keeps the resident running. Do not run the normal and test managers with conflicting automatic policies.

The SDK, packages and build caches stay under `.test-environment/`. SDK source metadata and its SHA-512 hash are preserved in `downloads/`. [Microsoft's .NET 8 download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) is the source. No machine-wide SDK installation or persistent PATH change is required. The existing Xiaomi and XiControl source directories are not edited.

## Rebuild

Exit the test tray resident, then double-click **Rebuild test.cmd**. It prepares the local SDK if absent, restores the declared packages and builds, printing progress and saving a timestamped build log. It does not launch the app. Initial preparation/package restoration needs internet access. To build from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test.ps1 -Prepare -Build
```

Launch from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\test.ps1 -View popup
```

The build and static contract checks have passed. No native app startup, visual/DPI validation, firmware change, driver launch or hardware acceptance run was performed during preparation. Full checks remain in `VALIDATION.md`. Xiaomi tools and proprietary settings still use the original manager's pages; standalone Toolbox hosting is not implemented.


Current daily deployment and measured hardware results supersede the earlier preparation-only statement above. Test launchers now prefer the current app build, using separate app/test-data. Exit the daily resident first; simultaneous daily/test hardware owners are rejected. See DAILY_RESIDENT.md.

# Xiaomi Revamp / PC Manager 0.2.0

Three independent Windows applications connected through PC Manager: **PC Manager**, **Screen Translator** and **AI Center** (semantic file search). PC Manager is required by the combined installer; either optional app can also be installed and run alone. All OCR, translation and search inference runs locally.

## Installation

Extract the offline installer ZIP. Keep `Xiaomi-Revamp-Setup.exe`, `packages.json`, `Uninstall.exe` and the selected component ZIPs together. Run `Xiaomi-Revamp-Setup.exe`, choose the applications, installation folder, data folder, optional separate AI Center data folder, current-user/all-users scope and startup options. The default application folder is `C:\Program Files\Xiaomi Revamp`.

`AI-Center-Setup.exe` and `Screen-Translator-Setup.exe` install the corresponding app independently. Installation into Program Files, all-users registration and PC Manager hardware/startup controls require administrator approval under the same Windows account. WebView2 Runtime must already be available. Setup installs no system Python/.NET packages and downloads no models.

The **Keep editable Development folders** option additionally requires `development-toolchain.zip` beside setup. Download that asset separately when the combined installer archive excludes it to meet GitHub's asset-size limit. It contains an existing .NET 8 SDK, offline NuGet cache and WebView2 references. No environment creation or package installation is necessary.

```text
Xiaomi Revamp/
  PC Manager/
    PCManager.exe
    Uninstall.exe
    runtime/
    Development/                editable source, Launch.cmd, copied toolchain
  Screen Translator/
    ScreenTranslator.exe
    Uninstall.exe
    models/, runtime/
    Development/
  AI Center/
    AI Center.exe
    Uninstall.exe
    models/, runtime/
    Development/
  suite-install.json            internal installation/data-path marker
  suite-owned.json              verified ownership metadata
  Uninstall.exe                 optional combined removal
```

An update preserves the previous app folder under `.previous-*` for recovery and preserves existing Development edits. Data paths and user scope are retained during upgrades; choose a fresh installation to change them. Unselected apps remain installed. Recovery folders can consume substantial storage.

## Data and uninstall

Default profiles live under `%LOCALAPPDATA%\XiaomiRevamp\install-<root-hash>`. A custom data folder uses `Users/<Windows SID>` to separate accounts. AI Center can use its own chosen folder, including a dedicated subfolder of Program Files. Setup grants write access to data and Development folders, while installed app binaries remain protected by the destination's normal permissions.

`pc-manager` stores preferences/artwork/logs; `file-search` stores configuration and `data/` with protected indexes, encrypted history and WebView state; `screen-translator` stores settings/artwork/logs/device choices/compiled caches; `shared` stores shortcut bindings, leases and fixed action queues. Models remain in each app's `models` folder. The installer refuses unmarked existing data folders rather than taking ownership of unrelated files. DPAPI-protected search state belongs to its Windows account and is not portable to another account.

Use Windows Settings or the selected app's `Uninstall.exe`. Each uninstaller stops its app and removes Windows registration and shortcuts. Independently choose whether to remove **your settings/index/history/cache**, **Development and its local archives**, and **changed/added app files**. Unselected apps remain intact and regain standalone shortcut handling if PC Manager is removed. Other Windows accounts' data is retained. Selecting changed/added app removal also removes verified previous-version recovery folders for those apps. The uninstaller verifies dedicated data ownership and refuses junctions before recursive removal. Existing user folders outside the chosen data paths are not removed.

## Connected behavior

`app settings -> shared local bindings -> one shortcut owner -> fixed action -> independent EXE`

PC Manager owns optional-app shortcuts while running. Each optional app takes over when the hub exits or crashes. Locks, process identity and a heartbeat prevent duplicate registrations; handover takes a few polling intervals rather than occurring atomically across Windows. Edits in an optional app appear in PC Manager and edits in PC Manager update the app's saved setting.

Keyboard settings provide a list of valid presets and a final **Press a shortcut** recorder. The recorder handles physical combinations, Copilot and Double Ctrl. Assigning an occupied app shortcut swaps the two keys while preserving their actions, including custom PC Manager actions. Invalid, reserved and externally occupied ordinary Windows chords leave the prior binding intact. Other software's keyboard hooks can still intercept physical keys.

The translator quick-panel link invokes translation immediately with settings hidden. Cold actions queue until models are ready. A second toggle cancels loading. After dismissal, original view, opening settings or stopping filtering, 10 seconds idle releases all owned inference workers; Stop releases them immediately. Compiled OCR/translation caches stay on disk. Cached reload measured approximately **16 seconds** on the tested laptop, so a 5–10 second reload is not guaranteed.

Toolbox provides app launch/settings/status and opt-in shared theme/accent. Companion apps started by the elevated PC Manager inherit its Windows privileges; standalone launches use their own launch context. Models, indexes, documents, screenshots and firmware access stay in their app; search queries and captured content are not exchanged through the shared shortcut layer.

## Editable development

Edit an app's `Development/source/<component>` and double-click `Development/Launch.cmd`. It rebuilds from source, copies offline runtimes/models on the first launch, stops the installed copy and starts `Development/App`. The normal installed profile is used when the installation remains present. If that profile/installation was removed, development uses its own `_data` folder. The installed binaries are not replaced.

```powershell
powershell -NoProfile -File '.\Development\Launch.ps1' -BuildOnly
```

Return to the installed version by quitting development and launching the EXE in the parent app folder. Local `Development/Archives` retains original sources and selected personal data during this laptop's migration; these archives are excluded from public GitHub files.

GitHub branches share one history: `manager/main` is the parent integration branch, `manager/screen-translator` and `manager/ai-center` identify the child components. Git branches are flat refs; branch names and documentation express this relationship. The repository's default `main` contains the complete integration.

## Source, environment and build

`source/pc-manager`, `source/screen-translator` and `source/file-search` contain copied app sources. `source/shared` contains the shortcut layer, recorder, launcher and installer. `tools/build_suite.py` compiles/stages/seals releases; `tools/launch_development.ps1` supports editable launches. `checks` holds focused validation. Generated outputs go to `build`, `install`, numbered `packages` and numbered `results`.

Supported and tested: Windows 11 x64, .NET Desktop 8, Python 3.12, WebView2 and .NET Framework 4.8 for setup/wrappers. The copied runtime payloads supply .NET/Python. Build requires an existing .NET 8 SDK, offline NuGet cache, WebView2 reference folder, existing AI Center payload and translator `zh-en` models. Search/translator requirements remain separate because their OpenVINO integrations differ. SDK/environment installation and driver changes are user-managed.

PC Manager requires supported Xiaomi MIFS/HID interfaces for firmware controls. The tested laptop is a Xiaomi Book Pro 14 TM2424. Search uses OpenVINO embeddings; translator uses OpenVINO OCR/translation. Intel GPU/NPU acceleration is optional and needs compatible drivers. The measured translator backend selected NPU detection, GPU recognition and CPU translation. A complete CPU-only desktop workflow, ARM64 and other operating systems have not been qualified. Model loading can briefly commit considerably more memory than its final working set.

```powershell
python tools/build_suite.py `
  --sdk 'C:\path\dotnet-sdk\dotnet.exe' `
  --package-cache 'C:\path\offline-nuget-cache' `
  --webview 'C:\path\webview-references' `
  --search-install 'C:\path\AI Center' `
  --translator-models 'C:\path\zh-en-models'
```

Run with an existing Python 3.12 interpreter containing translator dependencies. `--native-only` reuses staged private dependencies; add `--seal` to create a new numbered release. WebView references must include Core/WinForms DLLs and `runtimes/win-x64/native/WebView2Loader.dll`. Public source/development ZIPs exclude private settings, indexes, history, caches and generated builds. GPLv3/XiControl attribution applies to reused PC Manager code; optional-app licenses/notices remain included.

## Validation and known limits

The bundled translator is Chinese-to-English. Confident Chinese-only OCR units are eligible; English, other scripts, mixed-language boxes and uncertain OCR stay unchanged. This also rejects stale cached translations for ineligible text. Generated desktop labels at 18/26 pixels on light/dark backgrounds produced zero replacements, while a Chinese control remained recognized. Mixed-language labels may be missed deliberately; script checks cannot guarantee rejection of every confident OCR mistake or distinguish Han-only Japanese from Chinese. Reverse translation requires another compatible model and is not included.

With explicit authorization:

```powershell
python checks/check_suite.py --sdk 'C:\path\sdk\dotnet.exe' --dotnet 'install\PC Manager\runtime\dotnet\dotnet.exe'
python checks/check_deployment.py --release 'packages\NNN_UTC'
powershell -NoProfile -File checks\check_setup.ps1 -RunDirectory 'results\NNN_UTC_seed0'
```

The first check exercises real Windows hotkey ownership, automatic swaps, recorder events, crash/exit/restart fallback and recursive exclusions in scanning and SQL retrieval. Deployment checks verify every archived/staged file and private imports. Installer checks exercise real isolated install/upgrade/uninstall, all-users registration, Program Files data permissions and preservation of development edits. Native app checks exercise WebView controls, queued translation actions and real worker unload without capturing desktop pixels.

The retained full-drive search index is near the approximately **2 GiB protected-snapshot limit** and is saved as Paused. Existing results remain available. A full C-drive rebuild did not pass; supporting larger snapshots needs a storage-format change outside this release. Excluded folders are rejected themselves and recursively, including stale indexed results, while sibling-prefix folders remain searchable. Reboot/sign-in and physical Copilot/Double-Ctrl delivery have not been fully qualified. Synthetic OCR fixtures establish pipeline behavior, not general translation quality.

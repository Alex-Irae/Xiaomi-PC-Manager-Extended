# Xiaomi Revamp 0.3.7 (PC Manager 0.2.8, AI Center 0.3.5, Screen Translator 0.2.7)

Three independent Windows applications connected through PC Manager: **PC Manager**, **Screen Translator** and **AI Center** (semantic file search). PC Manager is required by the combined installer; either optional app can also be installed and run alone. All OCR, translation and search inference runs locally.

## New in 0.3.0

AI Center 0.3.0 stores its index on disk (EFS-encrypted by default) instead of in RAM, adds a Windows index channel with Windows semantic matches, a Programs category, and a faster meaning search. All three app EXEs gain `--disable`, `--enable` and `--status` for testing without start conflicts. Details, measurements and limits are in [RELEASE_NOTES.md](RELEASE_NOTES.md) and [source/file-search/README.md](source/file-search/README.md).

Upgrading: run the setup over the existing installation. Settings, history and the index are kept; each replaced app folder is preserved as `.previous-<component>-<time>` in the application folder until removed. The 0.2 search index is converted on the first start of AI Center 0.3.0.

## Installation

Download and run **`Xiaomi-Revamp-Installer.exe`**. This single offline file includes all three applications, their models/private runtimes, editable source and the complete offline development toolchain. Choose the applications, installation folder, data folder, optional separate AI Center data folder, current-user/all-users scope and startup options. PC Manager is mandatory in this combined installer. The default application folder is `C:\Program Files\Xiaomi Revamp`.

The alternative installer ZIP contains the same complete payload. Extract it and keep `Xiaomi-Revamp-Setup.exe`, `packages.json`, `payload.zip` and `Uninstall.exe` together. Identical payload files are compressed once and copied into each selected application's independent folder. Installed applications do not depend on another application's folder.

`AI-Center-Setup.exe` and `Screen-Translator-Setup.exe` install the corresponding app independently. Installation into Program Files, all-users registration and PC Manager hardware/startup controls require administrator approval under the same Windows account. WebView2 Runtime must already be available. Setup installs no system Python/.NET packages and downloads no models.

The **Keep editable Development folders** option includes an existing .NET 8 SDK, offline NuGet cache and WebView2 references. The complete EXE and its alternative ZIP already contain these tools. The separate `development-toolchain.zip` asset is provided for source-only downloads and older split-payload installers. No environment creation or package installation is necessary.

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

Keyboard settings provide a list of valid presets and a final **Press a shortcut** recorder. The recorder handles physical combinations, Copilot and Double Ctrl. Assigning an occupied app shortcut swaps the two keys while preserving their actions, including custom PC Manager actions. Invalid, reserved and externally occupied ordinary Windows chords leave the prior binding intact. The bottom confirmation appears after persistence and registration are confirmed; buttons return to Save. Blocked registrations report an error. An occupied key no longer disables unrelated app shortcuts, and registration retries when the other app releases it. Buttons have separate hover and pressed colors. Double Ctrl reconciles Windows key state before each tap so a missed key release cannot leave it permanently blocked. Local status records Ctrl event and gesture counts for diagnosis, without recording typed text. Other software's keyboard hooks can still intercept physical keys.

AI Center uses Keep loaded by default: the backend, embedding model and compact search WebView stay ready while its native resident runs. PC Manager starts that resident silently when Keep loaded is selected, respecting independent Windows-startup settings. Idle unload is still available; active/queued indexing pins the model regardless of that choice. AI Center's `--tray` launch is silent even when the app already runs. Both standalone Double Ctrl and the shared `file-search.open` action open compact search only. A direct EXE launch without arguments or PC Manager's `--center` invocation opens the main AI Center window.

In compact search, Enter (including Ctrl+Enter) submits the query and never opens a document.
Click a result or use its context menu to open it. The file-type selector restricts exact extensions;
`report pdf`, `pdf report` and `report .pdf` select PDF automatically. A single `pdf` lists PDF files.
Only first/last tokens select a type; a quoted `"pdf"` or a word in the middle stays searchable text.
Changing the selector removes an inferred boundary token; selecting All types clears that restriction.
Different types at both ends require clarification in the query. Existing `ext:` filtering applies
before ranking and the result limit; search/indexing/model algorithms are unchanged. Original typed
queries remain the keys for encrypted history and remembered-file ranking.

The translator quick-panel link invokes translation immediately with settings hidden. The screen shortcut displays the toolbox in the upper-right corner before model loading or OCR, including when models are already loaded. Its controls browser initializes only when opened and cannot stop an inference load if navigation fails. Cold actions queue until models are ready. A second toggle cancels loading. After dismissal, original view, opening settings or stopping filtering, 10 seconds idle releases all owned inference workers; Stop releases them immediately. Compiled OCR/translation graphs, validated hardware choices and Python bytecode remain on SSD in the selected data profile. Model hashes and the runtime/driver fingerprint are checked before reuse.

The bundled Marian ONNX encoder, decoder and KV-cache graphs now run directly through OpenVINO with NumPy greedy decoding, using the existing tokenizer and model weights. The general training framework is not imported by this path. On the tested laptop, full cached loads measured **7.30 and 7.39 seconds**. The measured load-time process-group peak fell from about **26.5 GB committed** to **1.83 GB committed**; working set and committed memory are different measures. Inference workers reach zero memory after idle release. An unopened hidden host measured about 16 MB private memory. These measurements are specific to this machine; first-time compilation or a driver/model change can take longer. Exact output equality passed against the preserved decoder on 22 public/generated cases.

Toolbox provides app launch/settings/status and opt-in shared theme/accent. Public EXE wrappers start through the Windows Explorer desktop broker so companions use the normal desktop profile and privileges, including when invoked by an application with redirected AppData. PC Manager requests elevation for its hardware controls separately. This keeps normal launches, startup and the elevated hub on the same installer-selected profile. Models, indexes, documents, screenshots and firmware access stay in their app; search queries and captured content are not exchanged through the shared shortcut layer.

## Editable development

Edit an app's `Development/source/<component>` and double-click `Development/Launch.cmd`. It rebuilds from source, copies offline runtimes/models on the first launch, stops the installed copy and starts `Development/App`. The normal installed profile is used when the installation remains present. If that profile/installation was removed, development uses its own `_data` folder. The installed binaries are not replaced.

```powershell
powershell -NoProfile -File '.\Development\Launch.ps1' -BuildOnly
```

Return to the installed version by quitting development and launching the EXE in the parent app folder. Local `Development/Archives` retains original sources and selected personal data during this laptop's migration; these archives are excluded from public GitHub files.

GitHub branches share one history: `manager/main` is the parent integration branch, `manager/screen-translator` and `manager/ai-center` identify the child components. Git branches are flat refs; branch names and documentation express this relationship. The repository's default `main` contains the complete integration.

## Source, environment and build

`source/pc-manager`, `source/screen-translator` and `source/file-search` contain copied app sources. `source/shared` contains the shortcut layer, recorder, launcher and installer. `source/shared/ui` holds `revamp.css` and `revamp.js`: the button states, bottom notice and undo/redo commands every app uses. Edit them there and run `python tools/sync_ui.py`, which overwrites the copy in each app's frontend folder (pass FileSync's `frontend` folder as an argument to update that app too). `tools/build_suite.py` compiles/stages/seals releases and refuses a stale copy; `tools/test/pc-manager/Launch.ps1` and `tools/test/screen-translator/Launch.ps1` build a `-test` copy of one app straight from `source` and run it with its own settings under `build/test`, with the installed app disabled meanwhile. The offline SDK, package cache and WebView2 references are expected in a `toolchain` folder beside this workspace. The workflow is: change `source`, try it with a test launcher, seal a release, install it; the installed apps keep no source of their own. `tools/launch_development.ps1` supports editable launches. `checks` holds focused validation. Generated outputs go to `build`, `install`, numbered `packages` and numbered `results`.

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

Wrap a sealed component release into a complete EXE and its alternative ZIP:

```powershell
python tools/build_inclusive_installer.py --previous 'packages\SEALED_COMPONENT_RELEASE' --output 'packages\NEW_INCLUSIVE_RELEASE'
```

This packager verifies prior component archives, seals explicitly selected app replacements, deduplicates files by SHA256, includes the complete development toolchain and embeds a verified ZIP behind a Framework bootstrap executable. Setup restores separate per-app folders and checks every restored file. `--changes` optionally accepts a JSON map of already-built app replacements. Installer tests support `-ContentStore` to exercise this format in isolated directories.

## Validation and known limits

AI Center opens the launcher; File Search opens the compact search bar. File search and AI Center are one program with two faces. File search (the shortcut and the search bar) starts hidden with Windows and has no tray icon of its own. AI Center is the launcher window and its tray icon; the icon appears once AI Center is opened and its menu offers AI Center, File Search and Quit AI Center. Quit AI Center removes the icon and the window and leaves file search working. Two settings in Search settings control this: "Start file search with Windows and keep it running without AI Center" (on by default; when off, quitting AI Center also stops file search) and "Start AI Center with Windows (tray icon)" (off by default). `AI Center.exe --quit` always stops everything.

Search settings shows the generated index location, saved checkpoint size/time and the separate pretrained embedding-model folder. Back up index chooses a destination and creates a new dated folder containing a consistent index checkpoint, configuration metadata, SHA256 manifest and recovery instructions. Backups retain file paths, change-tracking metadata, extracted text and vectors. They exclude original documents and model weights. Copying uses bounded buffers and leaves the live index available; capturing a fresh checkpoint still briefly needs the existing second SQLite snapshot in RAM. Wait for Backup complete before quitting. A folder without manifest.json is incomplete. Windows encryption requires the original Windows profile protection keys, so copying this backup to another laptop or a newly installed Windows profile is not a supported migration. For recovery, quit PC Manager and AI Center, preserve the current index, and follow RECOVERY.txt using the same file paths and compatible embedding model. Choose a backup destination outside application data so uninstalling the app does not remove it.

The bundled translator is Chinese-to-English. Confident Chinese-only OCR units are eligible; English, other scripts, mixed-language boxes and uncertain OCR stay unchanged. This also rejects stale cached translations for ineligible text. OCR recognition uses FP32 because GPU FP16 produced confident spurious Chinese on clear English labels on the tested Intel GPU. This precision change invalidates the previous hardware choice once; the first load can recompile and benchmark, then subsequent loads reuse SSD caches. Generated desktop labels at 18/26 pixels on light/dark backgrounds produced zero translation calls, while a Chinese control remained recognized. Native shortcut checks also compare English pixels before and after translation while requiring Chinese replacements. These checks deliver registered Windows hotkey messages and real shared commands in an isolated profile; physical keyboard delivery is a separate manual check. Mixed-language labels may be missed deliberately; script checks cannot guarantee rejection of every confident OCR mistake or distinguish Han-only Japanese from Chinese. Reverse translation requires another compatible model and is not included.

With explicit authorization:

```powershell
python checks/check_suite.py --sdk 'C:\path\sdk\dotnet.exe' --dotnet 'install\PC Manager\runtime\dotnet\dotnet.exe'
python checks/check_deployment.py --release 'packages\NNN_UTC'
powershell -NoProfile -File checks\check_setup.ps1 -RunDirectory 'results\NNN_UTC_seed0'
```

The first check exercises real Windows hotkey ownership, automatic swaps, recorder events, crash/exit/restart fallback and recursive exclusions in scanning and SQL retrieval. Deployment checks verify every archived/staged file and private imports. Installer checks exercise real isolated install/upgrade/uninstall, all-users registration, Program Files data permissions and preservation of development edits. Native app checks exercise WebView controls, queued translation actions and real worker unload without capturing desktop pixels.

Protected search checkpoints now stream compressed, authenticated SQL batches with a fresh DPAPI-wrapped AES-256-GCM key. They retain FTS shadow tables, vectors and metadata without a plaintext working file or one database-sized serializer allocation. Existing DPAPI/v2 checkpoints remain readable and migrate atomically on the next save. Saves copy stable pages in RAM, then compress/encrypt outside the live database lock. RAM still scales with index size and briefly holds a second database during checkpoints. Individual serialized rows are capped at 64 MiB; the normal indexed-text chunks are far smaller. A real 2.30 GB SQLite fixture passed encrypted save, reopen and further growth. This does not establish completion or semantic recall of a full C-drive rebuild. Excluded folders are rejected themselves and recursively, including stale indexed results, while sibling-prefix folders remain searchable. Reboot/sign-in and physical Copilot/Double-Ctrl delivery have not been fully qualified. Synthetic OCR fixtures establish pipeline behavior, not general translation quality.

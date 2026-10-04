# Xiaomi Semantic Search and AI Center

Current connected build: Double Ctrl and `file-search.open` show compact search only. Starting an existing instance with `--tray` leaves both window states unchanged. Launch the EXE without arguments or use PC Manager's settings invocation (`--center`) to open the main AI Center window. `--search` explicitly opens compact search. The native launch-routing check exercises real resident-instance messages without loading the browser or inference models.

Compact search uses Enter to search, never to open a file. Its file-type selector restricts exact
extensions. `report pdf`, `pdf report` and `report .pdf` select PDF automatically; `pdf` alone lists PDF
files. Only first/last space-separated type tokens select an extension, while quoted `"pdf"` and middle
words remain literal search text. Choose All types to remove an inferred token and filter. Different
types at both ends produce a query error. This uses existing `ext:` filtering before ranking/limiting;
indexing, embeddings and retrieval algorithms are unchanged. Original typed queries still identify
remembered-file choices and encrypted history.

This component is part of Xiaomi Revamp 0.2.0. Its released source, shared shortcuts, data paths and installer are documented in the [parent PC Manager README](../../README.md). Use the app's `Development/Launch.cmd` to rebuild and run edits with copied offline dependencies. The remaining notes document the standalone baseline and its original developer workflow.

This copy is the connected edition. The [parent README](../../README.md) provides the current installation/build commands and isolated data paths. Launch `install/AI Center/AI Center.exe --search` or `--center`. The shortcut is synchronized with PC Manager, which owns it while running; without the hub, the app handles it itself. Excluded folders include the folder itself and every descendant recursively, during scanning and retrieval. Optional theme/accent following and Open PC Manager are available in settings. Documentation below retains the component's historical standalone paths and release notes; use the Xiaomi Revamp installer and build tool for this edition.

**Development-only checkout.** The latest source/assets, current configuration, encrypted search index, models, tests and existing `.venv` are retained. Packaged releases/installers, old test results, legacy index backups and generated native builds were removed during cleanup. Historical release/result paths below are no longer present; installed applications outside this checkout are untouched.

Before using **Launch Search.cmd** or **Launch AI Center.cmd**, rebuild with the existing dependencies:

```powershell
pwsh -NoProfile -File native/build.ps1
```

The launchers need that generated native host. The retained `.venv` is the Python development environment, not a packaged application. Cleanup does not install packages or rebuild EXEs.

A local Windows file search with a compact Spotlight-style bar and a separate English AI Center. Search filenames, folders, extracted contents and meaning together. Each channel can be disabled independently, leaving at least one enabled. Exact names and the best matches appear first across file types, followed by category groups.

The desktop shell now uses **C# / .NET 8 / WebView2** with HTML, CSS and JavaScript, following the MiSans, white surfaces and blue accents of the revamped Xiaomi PC Manager. Python runs only the private indexing/retrieval backend. Xiaomi originals and previous UI sources remain preserved.

## Launch without a shortcut

### Installed edition

Version 0.1.7 keeps Double-Ctrl available through a small native background listener. The search bar never creates a tray icon or taskbar entry. The tray icon appears only while the main AI Center window is open, including while minimized; closing that window removes it. The native listener remains available without any visible icon. Launch AI Center from Start or the installed executable to reopen settings. Tray Quit or `AI Center.exe --quit` exits deliberately.

Keep loaded is the default: the hidden resident starts its backend, warms the existing embedding model and prepares the compact search WebView. Closing the search field keeps these resources ready; only a direct EXE launch or an explicit PC Manager Settings action opens the main AI Center window. Its settings WebView still releases after five seconds hidden. Idle unload remains an optional lower-memory mode, while queued or active indexing pins the worker/model in either mode. Model warmup uses existing local weights and compiled SSD caches, without dummy inference. The Double Ctrl listener runs on its own message-loop thread.

The installed root launcher waits for its native child and restarts it after a nonzero exit or forced termination, with a five-second delay. Recovery starts hidden. Explicit Quit and normal exit stop the supervisor. This is process crash recovery, not a Windows service: terminating both the supervisor and native host prevents recovery until relaunched or the next login. The development native executable alone has no supervisor. Launching with `--tray` starts without a window or browser. Accent colors propagate to center buttons, tabs, icons, search submission, category highlights, result selection and meaning badges through common CSS variables.

Click the AI tile beside the heading to select a PNG, JPEG, BMP or GIF, up to 10 MB and 32 megapixels. A centered 256-pixel PNG copy is saved as `%LOCALAPPDATA%\LocalAICenter\data\profile.png`; original images are untouched and nothing is uploaded. Reset picture restores the AI tile and retains the previous copy as `profile.previous.png`. The picture persists independently of search/index settings. Include these files in personal settings backups.

Start with Windows is controlled in Search settings. It registers `LocalAICenter` under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, launching `AI Center.exe --tray`. The app has no service and requests no administrator rights at login. Development previews and headless checks do not register startup. Uninstall removes this user's entry when it belongs to that installation.

Version 0.1.1 suppresses classic nonclient frame painting, removing the white outer strip while retaining native taskbar/minimize/maximize and resize behavior. Drive roots such as `C:\` can now be selected: Windows' Hidden+System flags on the drive anchor no longer exclude its entire tree. The same flags on actual subfolders still exclude them.

Drive-wide discovery uses an indexed Windows file-identity lookup, so discovering a new file does not scan every stored entry for a possible rename. Ancestor exclusions use a single metadata read per ancestor, retaining junction/symlink and Hidden+System rejection.

Incidental substrings in names have reduced ranking weight: `EFS` inside a folder named `refs` no longer outranks a paper matching EFS in both content and meaning. Exact filenames and stems still retain first priority. Setup checks that existing package files are writable and not in use before copying an update; close standalone CLI indexing jobs as well as the app before updating.

The October 1 read-only `C:\` inventory found 258,797 eligible files and 132,451 content candidates (3.64 GB), with 72 inaccessible folders. Existing exclusions include Windows, Program Files, dependency folders, this app's generated data/results/releases, `.ini`, `.dll`, binary-model/executable types, reparse points and super-hidden folders. Ordinary hidden folders remain eligible. These are account-accessible files after exclusions, not every file physically on the drive.

`tests/inventory_drive.py` counts this workload without modifying the installed profile. `tests/check_drive.py` builds a separate account-encrypted metadata snapshot, preserves the existing vectors, extracts/embeds a seeded bounded sample, then records hybrid/cached retrieval timings in `summary.json`, `timings.csv` and `latency.svg`. Complete metadata discovery does **not** mean complete content or semantic indexing. The current in-memory encrypted snapshot and exact vector scan are limitations for this volume; the drive test does not establish full-drive semantic recall.

```powershell
# Run from the source project. Each output folder must be new.
& 'C:\Program Files\Local AI Center\runtime\python\python.exe' tests/inventory_drive.py --root 'C:\' --config "$env:LOCALAPPDATA\LocalAICenter\config.json" --output results/NEW_RUN/inventory
& 'C:\Program Files\Local AI Center\runtime\python\python.exe' tests/check_drive.py --config "$env:LOCALAPPDATA\LocalAICenter\config.json" --seed-index "$env:LOCALAPPDATA\LocalAICenter\data\index.sqlite3.dpapi" --output results/NEW_RUN/drive --samples 24
```

The offline installer offers **Only for me**, **For all users**, and **Custom installation folder**. All-users defaults to `C:\Program Files\Xiaomi Revamp\AI Center`; current-user defaults to `%LOCALAPPDATA%\Programs\Local AI Center`. Custom locations can use either scope. Machine-wide installation requires the user's Windows administrator approval. The app is x64, so Program Files is the appropriate default.

Open **AI Center** or **File Search** from Start, or double-click `AI Center.exe` in the installed folder. For the bar directly:

```powershell
& 'C:\Program Files\Xiaomi Revamp\AI Center\AI Center.exe' --search
# Hidden resident listener:
& 'C:\Program Files\Xiaomi Revamp\AI Center\AI Center.exe' --tray
```

The package includes the existing Python 3.12 runtime, required local packages, .NET Desktop 8 runtime, Qwen3 embeddings, and the optional BGE export. It performs no online package/model download. Microsoft Edge WebView2 and optional Intel GPU/NPU drivers remain system prerequisites. CPU fallback is available.

Settings, encrypted index, query history, profile picture, compilation caches and browser profile live under `%LOCALAPPDATA%\LocalAICenter`. Each Windows user has a separate profile, and a new user's scan roots are empty until configured. Program Files contains editable application/source/assets, while Windows normally requires elevation to modify that machine-wide directory. The installer does not weaken its permissions. It installs Start menu links, a desktop link, and an Apps uninstall entry. Login startup uses the per-user Run entry; it does not change Xiaomi services or its shortcut setting.

An update preserves the previous application folder in a sibling timestamped backup. The included uninstaller removes the dedicated app folder, matching upgrade backups, private profiles and owned integrations after confirmation. Back up settings and artwork first. Original documents are never removed. The EXE is unsigned and Windows may identify its publisher as unknown.

Rebuild from the source project with the existing runtimes:

```powershell
pwsh -NoProfile -File packaging/build-release.ps1
```

The script creates a new numbered `releases/` directory containing `AI-Center-Setup.exe`, `AI-Center-Installer.zip`, the verified payload, and SHA-256 release/package manifests. The ZIP contains the same installer EXE and README. No private settings or index are included. `packaging/Setup.cs` implements selection/elevation, `Install.ps1` verifies/copies/registers the app, `Launcher.cs` invokes the bundled .NET runtime, and `Uninstall.cs`/`Uninstall.ps1` perform complete app/profile/integration removal after confirmation.

### Development edition

Double-click **Launch Search.cmd** for the bar, or **Launch AI Center.cmd** for the compact launcher/settings window. Both live in this project directory. A second launch brings the requested window forward. The native shell grants its resident instance foreground permission, focuses the WebView input, and repeats input focus after first navigation completes. Reopening clears the query. Windows can deny foreground activation on a locked/secure desktop. The installed 0.1.2 live typing and query-reset checks passed after unlock. No Python terminal stays open.

```powershell
cd "C:\Users\Irae\Documents\Documents\Xiaomi Rebuild\xiaomi-semantic-search"
& '.\native\bin\Release\net8.0-windows\AI Center.exe' --search
& '.\native\bin\Release\net8.0-windows\AI Center.exe' --center

# Session-only alternatives:
& '.\native\bin\Release\net8.0-windows\AI Center.exe' --search --no-shortcut
& '.\native\bin\Release\net8.0-windows\AI Center.exe' --search --paused
# Cleanly exit an existing native instance:
& '.\native\bin\Release\net8.0-windows\AI Center.exe' --quit
```

The Python CLI desktop command remains a compatibility launcher for the native executable. It no longer starts the Python GUI host. Double-Ctrl invokes search while the native tray process is running. Its gesture ignores held/repeated Ctrl and Ctrl+C-style chords. Shortcut changes apply immediately. If Xiaomi's original double-Ctrl setting is also enabled, disable it in Xiaomi's settings so two listeners do not respond. This app does not unregister Xiaomi's listener or stop AIBroker.

The bar initially opens horizontally centered, around 20% down the active monitor's usable area. Drag the magnifier or results footer to move it. Clicking outside, activating another window (including Alt+Tab), or pressing Escape hides it. Only the small native shell stays resident; its tray menu offers Search, AI Center and Quit. A blank search bar does not start Python. The bar clears its query and results on every reopening. AI Center has integrated minimize/maximize controls, resizable edges, and F11 fullscreen with Escape to restore. Settings are available only in the separate AI Center. Its Overview also launches the installed Intel AI Playground at `C:/Program Files/AI Playground/AI Playground.exe` for chat/image generation. It does not embed or manage Playground models.

## Search and settings

- Typing starts a debounced search. Fast filename/content results appear first; local semantic results follow when available. A small Meaning badge marks results whose document embeddings contributed.
- Matching recent query chips appear only after typing, including an exact repeat, with up to five suggestions. Up to 20 completed, stable queries are saved locally; opening a result saves its query immediately. Each chip has a remove button, and Clear history resets queries and remembered file choices. The same reset is available under AI Center > Search settings > Privacy. Blank reopening shows no history. Query history and result caching are separate: history persists, while cached result objects live only in the current Python worker.
- All, Folders, Documents, Images, Audio, Video, Archives and Other separate result classes. All shows the best five across types before category groups.
- Up/Down selects, Enter and Ctrl+Enter submit search without opening files, and Ctrl+C copies the selected path when no text is selected. Click a result or use its context menu to Open, Show in Explorer or Open with.
- The bar uses Xiaomi's original white arrow path in a vibrant blue button, regular MiSans text and hidden scrollbars. Results still scroll by wheel or keyboard. The arrow's provenance is in `research/stock-arrow.json`.
- AI Center controls search folders, excluded folders/types, CPU/GPU/NPU/Auto, shortcut, indexing frequency, independent search channels, light/dark/follow-Windows theme, accent color, font and result spacing. Window controls use PC Manager's SVG style, including its four-corner expand icon. Maximized and fullscreen bounds reserve two physical pixels at the bottom for an auto-hide taskbar.
- Index now reconciles the selected roots immediately. Pause stops new indexing work. Reset index archives the current encrypted snapshot, clears derived records and rebuilds from source documents. The app asks for confirmation before resetting; original documents are untouched.
- `.ini` and `.dll` are excluded by default. Ordinary hidden folders, including AppData when selected within a root, are allowed. Hidden plus System folders are excluded within selected roots. Reparse points, symlinks and explicit exclusions remain excluded.
- Folder exclusions reject the folder and descendants. Extension exclusions reject both filename and content retrieval. Existing results disappear immediately; current extraction/inference is not forcibly interrupted.
- Scope and exclusion changes filter retrieval immediately. Live watchers are reconfigured without restarting. Existing files under newly added or unexcluded roots need Index now or the next scheduled reconciliation. Shortcut changes apply immediately. Login startup changes apply only in the installed edition; development never modifies the Windows startup registration.
- CPU uses CPU only; GPU/NPU can fall back to CPU. Auto follows `devices`, currently GPU, NPU, CPU. Hardware changes release the pipeline before the next inference.
- Keep loaded retains the backend, search WebView and embedding model while the native resident runs. Idle unload releases an unused backend on dismissal/30 seconds idle, but never interrupts queued or active indexing or pending saves. The embedding idle timer is separately 120 seconds and cannot release a model during indexing. Explicit Quit releases owned resources.

## Program flow

`Direct launch or shortcut -> compact WebView2 bar -> local RPC service -> filename/path + SQLite FTS5 + local OpenVINO embeddings -> exact-name-first rank fusion -> real files/folders and matching passages`

`Selected roots -> metadata discovery -> structured extraction -> persistent SQLite chunks and vectors -> change notifications for incremental updates`

The UI has no remote search endpoint. WebView2 maps local assets to `https://ai-center.local`; this is a virtual host, not an HTTP server. Native WebView messages become newline-delimited UTF-8 JSON over private stdin/stdout pipes to the owned Python backend. File actions and windows stay in C#. External navigation and permission requests are denied. A 64-query in-memory cache stores successful results. SQLite revision triggers invalidate cached queries whenever file metadata, contents, or vectors change; settings/model changes also change the cache key. Cached repeats avoid model inference and disappear when the process exits. Stored document vectors and OpenVINO compilation caches persist.

The default Keep loaded mode retains a ready worker and model; the alternative Idle unload mode stops unused workers after dismissal or 30 seconds idle. Realtime indexing reconciles once on backend startup and then watches changes continuously while that worker runs. Periodic/manual modes retain their configured scan schedule. Paused stops new work; Index now explicitly overrides mode constraints for its request. Closing or minimizing every app window does not interrupt active or queued indexing.

SQLite is the persistent file-tree snapshot: paths, Windows file identity, size, modification/creation timestamps, content digest and extraction settings. Unchanged files reuse extracted passages and vectors. Renames preserve file IDs and vectors where file identity is available and the old path no longer exists. Timestamp-only changes hash the file and reuse vectors when its contents are identical. New or changed content is extracted and embedded. Reconciliation still walks metadata throughout selected roots, but does not re-embed unchanged documents. A same-size edit that deliberately preserves all tracked timestamps can evade metadata reconciliation; change notifications normally queue it, but the current worker still checks those metadata fields. Reset rebuilds everything in that exceptional case. Model or extraction-setting changes can require backfill.

## Project structure

| Path | Responsibility |
|---|---|
| `Launch Search.cmd`, `Launch AI Center.cmd` | Direct native launchers |
| `native/Program.cs`, `AICenter.csproj`, `build.ps1` | C# window lifecycle, shortcuts, outside clicks, WebView2 and private backend process |
| `native/SearchHistory.cs` | Account-encrypted query history, remembered file choices and Windows Open with action |
| `native/Personalization.cs`, `assets/app-icon.svg`, `build-icon.ps1` | Local profile picture, login startup, Ctrl gesture and branded icon |
| `xiaomi_search/backend.py` | Headless stdio RPC, subscriptions and indexer lifecycle |
| `xiaomi_search/__main__.py` | CLI, progress logs, Unicode-safe terminal output |
| `xiaomi_search/desktop.py` | Preserved legacy Python host; not the current entry point |
| `xiaomi_search/service.py` | RPC, settings, result cache and query validation |
| `xiaomi_search/store.py` | SQLite metadata/FTS/vectors, exclusions and ranking |
| `xiaomi_search/protection.py` | Windows-account DPAPI encryption, RAM SQLite and atomic checkpoints |
| `xiaomi_search/indexer.py` | Read-only discovery, extraction scheduling and watchers |
| `xiaomi_search/extract.py` | PDF/Office/text/code extraction and bounded chunks |
| `xiaomi_search/embedding.py` | Local OpenVINO model, device fallback and idle release |
| `frontend/search.html`, `search.js`, `search-shell.css`, `compact.css` | Compact search bar, movable regions and results |
| `frontend/center.html`, `center.css`, `center.js`, `settings.js` | AI Center launcher and settings |
| `frontend/settings.html` | Preserved legacy settings page |
| `frontend/bridge.js` | Native RPC transport |
| `frontend/index.html` | Redirect to compact search |
| `frontend/legacy-library.html`, `library.*`, `frontend/xiaomi/` | Preserved previous UI implementations; not the launch targets |
| `vendor/` | Untouched copied Xiaomi resources and provenance hashes |
| `research/architecture.md` | Broker and bridge investigation |
| `tests/check_core.py`, `check_native_backend.py`, `check_frontend.cjs` | Retrieval/standby, stdio integration, and deterministic UI history/reset/reconnect checks |
| `tests/check_pdf_acceptance.py` | Real full-index PDF retrieval, lexical versus meaning-only queries, ranks and latency figure |
| `tests/check_index_privacy.py`, `benchmark_devices.py` | Incremental/channel/encryption checks and complete-index CPU/GPU/NPU comparison |
| `tests/power_compare.py` | Explicit GPU/NPU sensor comparison with balanced trials |
| `tests/benchmark_local.py` | Explicit real-corpus/device benchmark with progress and isolated native workers |
| `results/` | Numbered benchmark runs, configurations, CSV/JSON evidence and SVG figures |

## Environment, setup and hardware

Supported desktop: Windows 11 x64, Microsoft .NET Desktop Runtime 8, Microsoft Edge WebView2 Runtime and Python 3.12. Tested here with .NET runtime 8.0.7, existing SDK 8.0.425, OpenVINO 2026.4.0, OpenVINO GenAI 2026.4.0.0, NumPy 2.5.3 and watchdog 6.0.0. Backend dependencies are listed once in `requirements.txt`. The native entry point does not import pywebview or pynput. The preserved legacy host needs those packages separately if deliberately revived.

Hardware tested: Intel Core Ultra X7 358H CPU, Intel Arc B390 integrated GPU, Intel AI Boost NPU. A GPU/NPU is optional; CPU fallback works for the inspected model. Other drivers, exports and machines remain unverified.

The existing `.venv` was supplied by the user and reused. No environment, package or model installation was performed. Setup on another machine is the user's responsibility:

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
Copy-Item -LiteralPath config.example.json -Destination config.json
```


Build the native host with an existing .NET 8 SDK and Windows Desktop reference packs. On this machine `native/build.ps1` reuses the SDK and WebView2 assemblies from the manager project, reading them without changing that project. No NuGet package references or package installation are used. For another machine, supply equivalent existing paths:

```powershell
& .\native\build.ps1
# Or specify your existing SDK and WebView2 DLL directory:
& .\native\build.ps1 -Sdk 'C:\path\to\dotnet.exe' -WebViewReferenceDir 'C:\path\to\WebView2\app'
```

The WebView2 directory must contain Core/WinForms assemblies and `runtimes/win-x64/native/WebView2Loader.dll`. Output is `native/bin/Release/net8.0-windows/AI Center.exe` plus its runtime files. Keep the project together: the executable locates frontend/backend/config by walking up to the project root. This is a compact desktop application, not a standalone single-file distributable. Close the running app before rebuilding.

Preserve an existing `config.json`. Defaults select no roots. This machine's `config.json` is now configured to search **Xiaomi Rebuild**, excluding this app's `data`/`results`, translator `results`, dependency folders and `model_cache`. The original settings and empty application index were backed up under `results/005_20260930T153133Z_seed42/app-config-before.json` and `app-index-before.sqlite3`.

The default local export is `C:/ProgramData/MI/AIModel/AIModelSearch/3.1.7/qwen3_embedding_int8_sym`, with model/tokenizer XML/BIN files and `config.json`. Its weights occupy about 597 MB; runtime RAM, compilation cache and stored text/vectors need additional storage. Embeddings have 1024 dimensions, approximately 4096 bytes per stored passage before database overhead. Only unencrypted compatible exports are supported.

## Configuration and commands

`config.json` holds roots, `excluded_names`, absolute `excluded_folders`, `excluded_extensions`, `preferred_device` (`auto/cpu/gpu/npu`), `devices` (Auto order), `shortcut` (`none/double_ctrl/alt_space`), `indexing_mode` (`battery_saver/normal/paused`), `indexing_frequency` (`realtime/5_minutes/15_minutes/hourly/daily/manual`), `name_enabled`, `content_enabled`, `semantic_enabled`, `theme` (`system/light/dark`), appearance, `index_protection` (Windows account protection by default), `model_standby` (`idle_unload/keep_loaded`), `idle_unload_seconds`, model path and extraction limits. Frequency, scope, search-channel and appearance changes apply immediately. Settings Undo/Redo restores previous values in the native session. Model path and encryption-mode changes require relaunch. Keep loaded retains the native search WebView, worker and embedding model; active or queued indexing also pins the model when Idle unload is selected. Advanced model settings include `embedding_pooling` (LAST_TOKEN/CLS/MEAN), `embedding_padding_side` (left/right), `query_instruction` and `max_tokens`; use settings appropriate to the model export.

Default limits: 512 tokens per embedding, 1100-character passages with 160-character overlap, 32 MB per content-extracted file, 2,000,000 extracted characters and 3000 chunks per file. Larger/unsupported files remain searchable by metadata. System/dependency folder names, symlinks, junctions, reparse points and model/executable binary extensions are skipped. Folder-name exclusions are evaluated inside the chosen root, not above it.

```powershell
# Explicit read-only corpus indexing, filename/content only:
.\.venv\Scripts\python.exe -m xiaomi_search index --lexical-only

# Full semantic backfill; a large corpus can take minutes or longer:
.\.venv\Scripts\python.exe -m xiaomi_search index

# CLI retrieval and status:
.\.venv\Scripts\python.exe -m xiaomi_search search "exclude folders from indexing"
.\.venv\Scripts\python.exe -m xiaomi_search search "embedding.py" --lexical-only
.\.venv\Scripts\python.exe -m xiaomi_search status

# Alternate paths; global options precede the subcommand:
.\.venv\Scripts\python.exe -m xiaomi_search --config config.json --data data desktop

# Checks and benchmark, only when explicitly requested:
.\.venv\Scripts\python.exe tests/check_core.py
.\.venv\Scripts\python.exe tests/check_native_backend.py
node tests/check_frontend.cjs
# Hidden native checks write JSON to a chosen evidence folder:
& ".\native\bin\Release\net8.0-windows\AI Center.exe" --check-frame --data "results\NEW_RUN"
# Lifecycle check: use a paused, semantic-disabled fixture config and separate data.
& ".\native\bin\Release\net8.0-windows\AI Center.exe" --check-lifecycle --config "path\to\fixture.json" --data "results\NEW_RUN\lifecycle-data"
.\.venv\Scripts\python.exe tests/power_compare.py --phase-seconds 20 --repeats 2
.\.venv\Scripts\python.exe tests/benchmark_local.py --root ".." --documents 16 --timeout 240
```

Keep one index-writing job per data directory. Close the desktop before running CLI indexing on its index. A manual CLI index explicitly runs in normal mode; desktop backfill respects pause/battery settings.

Query filters: `ext:py`, `type:folder`, `type:pdf`, `type:docs`, `type:code`, `type:image`, `type:audio`, `type:video`, `type:archive`, `folder:"Research Notes"`, `before:2026-01-01`, `after:2026-01-01`. Dates use local modification time. Folder query filters are path-substring matches; exclusion settings use directory boundaries.

## Measured validation

**Run 010, full production index:** all devices retrieved the user PDF first for EFS, estimation-free and the score-function paraphrase, without fallback. Nine uncached queries per device used three queries with three repetitions, in sequential isolated workers. Document vectors were reused from GPU indexing. The final AES-GCM trial measured:

| Device | Protected index load | Model setup with current caches | Warm hybrid median | Cached median |
|---|---:|---:|---:|---:|
| CPU | 490 ms | 1.225 s | 735.2 ms | 1.42 ms |
| GPU | 474 ms | 1.497 s | 242.5 ms | 1.41 ms |
| NPU | 496 ms | 0.853 s | 315.2 ms | 1.52 ms |

GPU remains Auto's first choice. NPU warm latency was about 73 ms slower in the final trial, below the user's 200 ms comparison target. The first trial had a 64.5-second NPU compile and noisier GPU timings (468 ms median); warm NPU was 315.4 ms. The second trial reused newly generated NPU compilation caches. Cold compilation, index unlocking and UI debounce/rendering are excluded from warm medians. These are a few retrieval probes, not a broad quality evaluation or a power-efficiency result. Full configuration, progress, per-query ranks, CPU process time, CSV and a latency SVG are preserved in `results/010_20261001T034200Z_seed42_controls_index_privacy/devices01` and `devices02`. The figure compares median milliseconds per device; smaller bars indicate faster warm full-index retrieval. Setup costs and weak repetitions remain in the raw evidence. The AES-GCM envelope reduced protected-index startup from 11.8–12.9 seconds to 0.47–0.50 seconds.

Core checks passed: exact filename priority, FTS and synthetic semantic retrieval, folders, filters, HTML escaping, scope/exclusion denial, stale data, revision-based cache invalidation and bridge responses. A real Service/Embedder check also passed GPU retrieval, repeated-query cache hits, folder-only results and one-second test idle release. Unicode CLI JSON output passed after fixing the Windows console encoding.

The corrected corpus scan in **run 002** indexed 1156 eligible files, 120 folders and 34,937 passages in **53.7 seconds**, with no extraction errors. It excluded `model_cache` directories. The first scan without that cache exclusion found 2197 files and took 106.7 seconds. Its failed device setup is preserved in run 001; the missing benchmark cache directory was fixed before later runs.

**Run 003** reused the read-only corpus snapshot and tested 16 representative real passages on all devices, without fallback:

| Device | Cold pipeline load | Warm query embedding + sample comparison median | Full lexical index + sampled semantic hybrid median |
|---|---:|---:|---:|
| CPU | 2.35 s | 723.1 ms | 770.7 ms |
| GPU | 4.94 s | 22.2 ms | 70.2 ms |
| NPU | 68.13 s | 84.6 ms | 144.2 ms |

CPU process time during the document/query workload was 51.6 processor-seconds on CPU, 0.83 on GPU and 0.31 on NPU. Processor-seconds sum work across CPU threads; they are not utilization percentages. Query vectors closely matched CPU results: mean cosine 0.9984 for GPU and 0.9996 for NPU. These are cross-device consistency checks, not search-quality scores.

**Run 005** confirmed actual per-process GPU activity: one GPU engine reached approximately 100% during a separate six-second uncached inference loop. That sustained telemetry loop is excluded from query timings and does not represent ordinary typing or idle use. NPU utilization/power percentages were not available from the inspected Windows counters; explicit NPU compilation and inference succeeded without fallback. Battery energy was not measured in that run; run 006 adds limited GPU and package-power observations below. Run 004 preserves a failed Windows PowerShell counter-module attempt; run 005 used the installed PowerShell 7 successfully.

Run 002 median full-index lexical retrieval was **29.7 ms**, repeated cached retrieval **2.0 ms**. The production Service check in run 005 returned warm GPU hybrid queries in **69–87 ms** and cached queries in **2.0–2.4 ms**; its first query took 1.97 s including the existing compilation cache/model setup. Compilation caches make later launches different from the cold loads above.

**Run 008 completed the production application index:** 1,199 eligible files, 131 folders, 722 files with extracted contents, 477 metadata-only files, and **35,112 passages with 35,112 matching-model vectors**. All queues were empty, with no scan, extraction or semantic errors. The GPU backfill took about 22 minutes. This covers eligible files throughout Xiaomi Rebuild, subject to the documented exclusions and size limits. It includes the user-added 24-page PDF at `../clickntranslate-1.8.1/clickntranslate-1.8.1/icons/DATA GENERATION WITHOUT FUNCTION ESTIMATION.pdf`, outside this project. Both EFS and estimation-free returned it first in the visible bar and in lexical/hybrid service checks. The earlier UI test configuration had semantic retrieval disabled; ordinary launch uses semantic-enabled `config.json`.

**Run 009, initial production snapshot, verifies meaning-only retrieval and fixes ranking:** the PDF was absent from lexical results for “learningless synthesis utilizing collective repulsions” and “synthèse déterministe échantillonnage sans apprentissage”. Before the correction, hybrid ranked it 8th/24th; afterward it ranked 1st/2nd, with only Semantic in its match labels. General lexical votes now scale with squared query-word coverage, and semantic votes retain cosine confidence, preventing a single incidental bundled-code word from receiving a full lexical vote. Exact filename priority remains first. No PDF-specific path, type boost, or model substitution was added. EFS, estimation-free, and the English score-function paraphrase still ranked it first.

On the full 35,112-passage production index, the final GPU run measured 329–637 ms for warm, uncached hybrid queries, about 2.58 seconds for the first hybrid query including setup, and 1.4–3.9 ms for cached repeats. These are service timings, excluding UI debounce/rendering, and are not the earlier 16-passage sample timings. Cache hits last only until worker termination. The rank and timing evidence is in `results/009_20260930T175919Z_seed42_search_revision/full-index-confidence02.json`, `.csv` and `.svg`. These few successful queries establish this acceptance task, not broad semantic recall.

Results are preserved separately, including complete configurations, progress logs, `timings.csv`, `summary.json`, `latency.svg`, selected passages and the small vectors required for cross-device comparison. The figures show milliseconds horizontally, with shorter bars indicating faster warm retrieval; device bars cover query embedding and comparison on the 16-passage sample, while lexical bars cover the full eligible lexical index. Cold load time is excluded. They do not establish full-corpus semantic recall or million-file scaling.


**Run 006: GPU versus NPU power observation.** Two trials per device used balanced GPU, NPU, NPU, GPU order. Each excluded compilation/warmup, then measured 20 seconds of loaded idle and 20 seconds of uncached queries every two seconds. Intel Graphics Control Library GPU energy differences and Windows RAPL package counters were sampled read only. Raw logs, JSON, additional CSV and `power.svg` are preserved in `results/006_20260930T162255Z_seed42_power`.

| Device | Loaded-idle package mean across the two trials | Periodic-query package mean across the two trials |
|---|---|---|
| GPU | 3.54 / 2.13 W | 4.91 / 2.08 W |
| NPU | 2.84 / 4.85 W | 5.63 / 2.35 W |

These are sensor-domain readings, not whole-machine power. Drift between repetitions is larger than a reliable device advantage; some active phases measured below their own idle phase. The machine was on AC, reporting zero battery discharge. Independent NPU watts and wall power were unavailable. GPU-domain means during NPU queries were lower, as expected when work moves off the GPU, but that does not establish battery savings. The conclusion is **inconclusive**. Loaded versus unloaded idle and 24-hour battery operation were not tested, so Auto remains GPU-first and Keep loaded is optional. The power figure's horizontal axis is watts, with shorter bars meaning lower observed package draw; inconsistent baselines visibly invalidate an efficiency claim.

**Native UI validation:** Release build completed with zero warnings/errors. Core retrieval and standby-policy checks passed, as did a real Unicode stdio-backend round trip, native-action acknowledgement and backend shutdown. At the observed 192-DPI display, AI Center rendered at the intended size, the full search arrow remained inside the bar, typing expanded results, folder filtering worked, dragging moved its screen origin, and an outside click removed the visible search window. `--inspect-ui` only exposes a temporary taskbar entry for accessibility inspection; ordinary launches hide that entry. A real first semantic UI query took about 28.5 seconds on this session, including model setup, so prior warm-query medians are not first-use latency guarantees. UI screenshots were visually inspected; there is no screenshot pixel comparison test.

The subsequent run 009 refresh covered 1,231 files, 139 folders and 35,187 fully embedded passages with no errors. Both required PDF terms remained first. The two original meaning-only probes ranked the PDF fourth/fifth after our new test sources and documentation were indexed; those sources explicitly contain the probe phrases and now compete as valid matches. Four fresh descriptions were therefore executed inline, with query strings saved only in excluded results. One found no PDF lexically but retrieved it first by Semantic alone; a French description improved from lexical rank 15 to hybrid rank 2; the other two returned the PDF first. All probes, including weaker ranks, are preserved in `results/009_20260930T175919Z_seed42_search_revision/held-out-descriptions.json`. These checks support real meaning retrieval while exposing sensitivity to corpus composition.

## October 1 battery and added-file checks

Run 011 compared GPU, NPU, NPU, GPU on battery. Each device had 45-second unloaded idle, loaded idle, periodic query-embedding and sustained document-embedding phases; the first eight seconds were excluded. The same 14 real PDF passages were cycled on both devices. Compilation/warmup was separate. Windows BatteryStatus measured whole-machine discharge, Intel IGCL measured GPU energy, and Windows RAPL supplied package counters. These domains must not be added together. Original Xiaomi services and other apps remained running.

| Document embedding | GPU trials | NPU trials |
|---|---|---|
| Whole-machine average draw | 34.4 / 39.6 W | 16.8 / 14.7 W |
| Whole-machine energy per completed passage | 0.76 / 0.80 J | 1.85 / 1.65 J |
| Median individual inference | 19.6 / 18.1 ms | 56.5 / 56.7 ms |

NPU reduced instantaneous draw but completed less work. GPU used about 55% less whole-machine energy per passage in these repeated embedding phases, so Auto still prefers GPU. Baseline-subtracted energy also favoured GPU. Idle and occasional-query readings drifted and do not establish an NPU energy advantage. This is not a controlled battery-life estimate: battery telemetry is smoothed, other apps remain active, independent NPU watts are unavailable, and extraction, complete retrieval, UI work and encrypted checkpoints were excluded. Frequent unchanged-file checks avoid model inference rather than keeping an accelerator busy continuously. Raw configuration/samples, CSV and watts figure are in `results/011_20261001T045826Z_seed42_power/`; `energy.svg` compares joules per passage, where shorter bars mean less energy for completed work.

Run 012 evaluated 15 descriptions against 14 new research PDFs and one YAML file. Neither filenames nor expected labels were passed to the embedding/rerank models. The same 169 evenly sampled passages were used for both embedding models, with maximum passage similarity per file. Qwen3 and BGE-small each placed 14/15 intended files first and 15/15 within the first five. Their peptide-related mistakes differed. Correctly prompted Qwen reranking of ten candidates placed 15/15 first, adding a median 232 ms of inference. This added latency exceeds the user's 200 ms comparison threshold, so reranking is not enabled by default. Its setup cost and prompt/token-budget preparation are additional. The original incorrectly prompted reranker trial is explicitly marked invalid and preserved in models01; use models02.

BGE-small is an optional English alternative with 384-dimensional vectors, CLS pooling and right padding. The downloaded export is from [LAION's OpenVINO export](https://huggingface.co/laion/bge-small-en-v1.5_openvino_int8), pinned and checksum-verified in `data/models/bge-small-en-v1.5-int8-ov/download-manifest.json`; its retrieval instruction follows the [BGE model card](https://huggingface.co/BAAI/bge-small-en-v1.5). No additional packages were installed. Qwen remains the production default. The small-folder comparison does not establish BGE's complete-desktop recall or battery efficiency.

For an explicitly chosen BGE rebuild, update these fields in a copy of config.json and use a separate data directory:

```json
{
  "model_path": "C:/absolute/path/to/xiaomi-semantic-search/data/models/bge-small-en-v1.5-int8-ov",
  "embedding_pooling": "CLS",
  "embedding_padding_side": "right",
  "query_instruction": "Represent this sentence for searching relevant passages: "
}
```

```powershell
.\.venv\Scripts\python.exe -m xiaomi_search --config config-bge.json --data data-bge index
.\.venv\Scripts\python.exe -m xiaomi_search --config config-bge.json --data data-bge search "your description"
```

Exclude the separate data directory from selected scan roots. Changing model/pooling/instructions invalidates vector compatibility; vectors from different profiles are never mixed. Keep Qwen's current data directory until comparison is complete. No model loads just from configuring one.

The full Qwen index refresh covered 1,248 eligible files, 140 folders and 36,629 fully embedded passages with empty queues and no extraction/indexing errors. In full-index checks, lexical search placed 12/15 intended files first, combined search 14/15, and meaning-only search 13/15; all 15 were within the first five for combined and meaning-only retrieval. The YAML improved from lexical rank 65 to combined rank 4 and meaning-only rank 2. Nearby configuration files and related peptide papers remain valid competitors; these expected-file labels are not exhaustive relevance judgments. Warm median service times were 437 ms combined and 253 ms meaning-only, excluding UI debounce/rendering; first combined use took 3.0 seconds. Evidence is in `results/012_20261001T051300Z_seed42_focus_models/full-index01/`. These are author-written descriptions, not a blinded general-quality benchmark.

The newly built native shell and frontend checks pass. The lifecycle check verifies no worker before the five-minute interval, a due reconciliation, worker shutdown on completion/dismissal/idle, and protected history. An unchanged full-tree check took 5.24 seconds plus 0.56 seconds to unlock the index, preserved its revision and all 36,635 vectors, and loaded no embedding pipeline. The extra six passages follow the final documentation refresh. Live typing immediately after invocation still needs an unlocked desktop; source and simulated DOM focus checks do not prove Windows foreground behaviour.

To repeat model or full-index checks, use a new output directory:

```powershell
.\.venv\Scripts\python.exe tests/compare_models.py --cases results/012_20261001T051300Z_seed42_focus_models/cases.json --output results/NEW_RUN/models --corpus ../screen-translator/ToRead
.\.venv\Scripts\python.exe tests/check_corpus_search.py --cases results/012_20261001T051300Z_seed42_focus_models/cases.json --output results/NEW_RUN/full-index
```

## Privacy, outputs and limitations

The snapshot uses Windows CNG AES-256-GCM with a fresh random key per checkpoint. DPAPI protects that small key for the Windows account; it does not encrypt the entire 250 MB payload directly. This removes the measured bulk-DPAPI startup bottleneck. The original DPAPI-only format remains readable and upgrades on the next checkpoint. Crypto is provided by Windows, without a new Python package; the binding passed a known-answer AES-GCM check and authenticated tamper tests.

Indexed source documents are read only. By default the live index is `data/index.sqlite3.dpapi`, containing encrypted metadata, extracted text and vectors. Windows DPAPI binds decryption to the Windows account, so the app unlocks automatically without a password prompt. Hashing a password twice would not encrypt an index. [Microsoft's DPAPI documentation](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) describes account-based decryption and roaming-profile qualifications. A copied index alone cannot be opened as ordinary SQLite; it is not a portable password-protected backup. Back up Windows encryption credentials separately if account recovery is needed. Applications running as the same account can decrypt it, and documents themselves remain unencrypted.

SQLite runs in RAM while unlocked. Authenticated snapshots are checkpointed every 15 seconds when dirty and on graceful shutdown, through an atomic encrypted pending file. A forced process termination can lose uncheckpointed derived changes; metadata reconciliation repairs them. The approximately 250 MB index requires additional RAM and decryption time on each worker start. There is no plaintext working SQLite/WAL/SHM file in protected mode. First-use migration preserves the old index and sidecars in encrypted `legacy-index-backups/`; reset preserves an encrypted `data/index-archives/` snapshot. Neither archive is scanned. Only one process can open a protected index at once.

New query history uses `data/history.dpapi` with the same account protection. Old WebView localStorage history is migrated and its key removed after successful encrypted saving; this does not establish forensic erasure of the old browser profile. Configuration, model compilation caches, browser profile, logs, memory/pagefile and original documents are outside this encryption boundary. Logs may contain error paths. Historical numbered benchmark results contain plaintext query strings, paths and sample passages and are preserved; exporting the entire project therefore exports those artifacts too. UI assets are local with a restrictive content-security policy; HF offline flags are enabled. Physical network-disconnection/firewall testing was not performed.

The installed Xiaomi search already uses semantic components. AIService supervises AIBroker, which owns search IPC and the original shortcut. This app reads the existing unencrypted Qwen3 embedding export through OpenVINO directly. It does not call the broker, use Xiaomi's GPU reranker/segmenter, or stop their services. Existing broker GPU activity may therefore remain while this independent app is running.

Retrieval fuses filename/path, coverage-weighted content BM25 and cosine-weighted semantic channels, with exact filenames/stems first. It streams exact vector comparison from SQLite in bounded batches, O(passages × dimension), with no ANN index. Search returns at most 500 candidates. Semantic threshold 0.3 and ranking weights are provisional. The earlier 16-passage benchmark and five full-index PDF queries are insufficient to establish broad English/French recall; unrelated bundled resources can still appear. Whole-index vector comparisons currently take hundreds of milliseconds and first use needs model setup. Auto prefers the fastest tested device here but does not dynamically benchmark or choose for power efficiency.

PDFs require extractable text; scanned-PDF OCR, image semantic search, audio transcription, video processing, encrypted documents and thumbnails are not implemented. These types can be returned by filenames/metadata where eligible. Text PDF extraction passed on the actual user document. Office extraction paths exist but the corpus benchmark did not contain each supported Office format. XLSX reads stored values, not recalculated formulas. Unsupported or oversized content remains metadata-only. A changed model fingerprint requires vector backfill.

Run 010's isolated checks cover all seven nonempty search-channel combinations, stable metadata, rename/timestamp vector reuse, changed-content invalidation, hidden versus Hidden+System folders, default extension exclusions, forced indexing, encrypted reload/tamper rejection and reset with an encrypted archive. Native hidden checks cover rounded regions, disabled DWM nonclient rendering, minimize/maximize styles, Search ToolWindow/no-taskbar styles and the two-pixel bottom activation strip. Lifecycle checks start and stop actual owned backend processes, including a scheduled metadata reconciliation and encrypted history round-trip. Node checks exercise history, stale-result cancellation, themes, channel validation, indexing controls and native window commands. The Windows desktop was locked during live inspection, so the final rendered controls, corners and physical taskbar hover remain visually unverified. Alternate-monitor DPI transitions, broad watcher recovery, actual Playground startup and battery-life benefit remain untested. No original services, startup tasks, registry or firewall settings were changed.


## Whole-PC indexing and exclusion files (0.1.5)

Search settings has **Index all files on this PC** beside the included/excluded folder controls. It saves the roots of available fixed local drives, including `C:\`, as the included scope. It does not start a full scan itself: press **Index now**, or wait for the configured scheduled reconciliation. It does not include network or removable drives. Existing excluded paths, directory names, extensions, reparse points and hidden+system folders remain excluded. Ordinary hidden folders are eligible. Inaccessible folders are skipped and reported; indexing continues. Names become searchable during discovery, contents after extraction, and meaning after embedding. A large drive can take substantially longer to embed than to discover.

Choose **Include** or **Exclude**, enter one absolute folder path, and press **Add**. **Manage** opens a GUI with one editable path per row, a remove button, Undo/Redo, Save and Cancel. Save commits the dialog as one settings step. The backing files are `<profile>/included-folders.txt` and `excluded-folders.txt`, with real line endings and one absolute path per line. Blank lines and whole-line `#` comments are supported. Existing JSON lists migrate once. Old comment-led files containing literal CRLF escape text are repaired. Invalid or relative paths are rejected. Newly included or unexcluded files need an indexing pass. Built-in exclusions still apply. Reset picture is under Appearance.

Development launch remains `Launch AI Center.cmd` or `Launch Search.cmd`. Installed launch is `"C:\Program Files\Xiaomi Revamp\AI Center\AI Center.exe" --center` or `--search`. Installed private settings and both scope files are in `%LOCALAPPDATA%\LocalAICenter`; the encrypted index, logs, history, artwork and WebView cache are under its `data` directory. Do not launch a second CLI index against the live index.

**Uninstall AI Center.exe** prompts before deleting private data and requests administrator elevation for a machine-wide install. The complete removal script runs outside the installation to remove its own installed EXE too. It removes only dedicated app paths whose package identity is LocalAICenter, and does not traverse junctions. Explicit OEM service tuning is independent of installation and remains in place. External user-created backups and the development workspace are preserved. Shared runtime installations are not removed.

The 0.1.6 executable, native apphost, tray, shortcuts and installer use a transparent glossy silver variant of the supplied Xiaomi XiaoaiAgent Logo.ico. The original icon is preserved in `native/assets/xiaomi-original.ico`; the built-in imagegen edit is saved as `native/assets/xiaomi-silver.png`. `native/build-icon.ps1` encodes 16/24/32/48/64/128/256-pixel ICO frames from that asset. The user profile picture remains separate and customizable.


## Development validation, October 2

Release 0.1.7 packages the validated development fixes below, including folder summaries, context actions, persistent history and query-specific file preferences. Use `Launch AI Center.cmd` for development settings or `Launch Search.cmd` for its bar, from this project. The native development executable keeps the shortcut resident while hidden, but has no installed supervisor and does not alter Windows login startup. Its configuration, included/excluded files and `data/` are separate from the installed profile. The current development scope is C:; new installed profiles start with no included roots. Avoid running installed and development shells together because they share the native single-instance identifier. Clean reinstall removes old installed app/profile/cache/index state after a validated settings backup, then restores selected configuration, folder lists, history and artwork. Fresh indexing must complete before full semantic coverage is available.

Run 019 preserves the failing installed configuration, malformed exclusions, original encrypted-index hash, and an isolated copy of its 251,350-file metadata snapshot. The old exclusion file had literal CRLF text on a comment line, effectively ignoring its paths. Settings saves also performed full filesystem reconciliation while holding the index lock, and semantic work waited behind the entire extraction queue. The fixes repair scope files, filter retrieval directly in SQL, move reconciliation filesystem checks outside the database lock, and interleave small embedding batches with discovery/extraction. Unchanged fully embedded files do not reload the model.

A bounded 60-second real CPU scan of C: made semantic progress from zero to 85 passages, including a separately seeded 24-passage real PDF acceptance check. It extracted 93 documents and retained 251,253 pending files. Theme writes during work took 4.42–7.35 ms. EFS, estimation-free and a meaning-only description retrieved the intended PDF first. This demonstrates progress and responsive preferences, not a completed whole-drive index. Full-drive semantic coverage, sustained completion and million-passage retrieval remain unverified.

The selective filename predicate reduced EFS lexical retrieval on the same metadata snapshot from 609.9 to 41.3 ms. Later GPU hybrid probes took 80.3–217.8 ms warm, 3.2 seconds including first model setup, and 0.8–2.66 ms from the worker cache. Only 85 passages were embedded in this partial snapshot; these numbers cannot estimate fully embedded C: latency or establish a controlled CPU/GPU/NPU or power comparison. Exact vector scanning and the RAM-resident encrypted database remain the major scale limits.

Native WebView checks validate light/dark/Windows themes, immediate writes surviving close, settings undo history surviving browser disposal, path dialog editing, whole-PC scope without a scan, foreground-loss dismissal and a separate shortcut message loop. Gesture logic is checked; physical modifier-only Ctrl+Ctrl injection was not performed. Backend checks cover progressive embeddings before discovery completes, worker lifetime, unchanged snapshot reuse, live root watcher replacement, reset recovery, seven channel combinations, Unicode RPC and encrypted reload/tamper rejection. Evidence is preserved under `results/019_20261002T020839322_development_ux_index/`; prior failed attempts remain available.

For a development hang diagnosis, start the native shell from a PowerShell session with `$env:AI_CENTER_TRACE_SECONDS = '20'`. Its owned backend then logs thread stacks every 20 seconds to `data/native.log`; paths can appear in these diagnostics. Omit the variable for normal operation. Normal startup logs its stages and emits one delayed stack only if it has not become ready within 30 seconds. Index discovery logs every 100 processed files. This diagnostic option neither changes the saved configuration nor creates a scheduled task.

The live development launch uncovered an additional stall that the in-process model checks missed: NumPy's native-module import blocked on the background indexer thread after about 700 files. Saved repeating thread traces identify this import. The stdio backend now initializes NumPy on its main thread before checkpoint/watch threads start; this initializes CPU numerical support without loading a model. The actual desktop then completed discovery of 1,650 eligible files and drained its embedding queue. A subsequent unchanged reconciliation finished in about 3.3 seconds without loading the model. `tests/check_native_backend.py --semantic --output results/NEW_RUN/stdio-semantic` now checks real background embedding and semantic RPC, saving its configuration and logs.

On the refreshed full development index, EFS found the intended PDF second, estimation-free first, and the tested descriptive query second. Test sources containing the same phrases compete legitimately: the descriptive query's first result is the diagnostic script itself. These corpus changes limit a first-place claim but do not indicate missing semantic coverage. Full-development warm service probes took 287–419 ms, and the initial query took 1.67 seconds including model setup. The faster partial-drive sample timings above must not be substituted for these full-development measurements. Actual ranks and competitors are saved in `development-acceptance.json`.


## Indexing progress

Search settings displays Overall known work, Indexing and Embedding percentages.
Indexing counts resolved file attempts, including unsupported, empty and failed
files; failures are shown separately. Embedding counts stored vectors against
extracted text passages. Overall combines files and passages with equal weight,
not elapsed time. Totals grow during discovery, so a percentage may decrease and
100% known coverage does not mean the drive scan has finished. Existing indexed
files can still be checked for changes.

Approximate remaining time uses up to two minutes of observed progress after
at least 30 seconds. It estimates only known remaining work, and disappears when
paused, blocked or stalled. It is unavailable for undetected work or a different
embedding model's coverage. No extra database queries or changes to the indexing
worker are introduced. Reopening a settings window resets its throughput sample.
Run focused checks with the existing Node runtime:
`node tests/check_index_progress.cjs` and `node tests/check_frontend.cjs`.

## Folder summaries, file actions and remembered choices

The installed AI Center entry opens the main launcher. File Search opens only the compact bar. AI Center keeps a tray icon while resident; use its menu to reopen the launcher/search or quit the process.

Search settings includes Index and backup. It displays the generated `index.sqlite3.dpapi` location, saved size/time and the distinct pretrained model folder. Back up index creates a new dated folder in a location you choose outside application data. It preserves the complete metadata, extracted text, vectors and index-related settings, with a SHA256 manifest and RECOVERY.txt. The backup excludes original documents and model weights. Indexing can continue during copying; checkpoint capture uses the existing RAM snapshot. Wait for Backup complete before quitting. Folders without manifest.json are incomplete. DPAPI encryption requires the original Windows profile keys; another device or reinstalled Windows profile is not a supported recovery target. Stop PC Manager and AI Center before restoring, retain a copy of the existing index, and follow RECOVERY.txt with matching file paths and compatible model weights. Focused validation: `runtime/python/python.exe -B tests/check_backup.py --output results/NEW_RUN` and `node tests/check_frontend.cjs`, using the existing bundled runtimes.

Included and excluded folder summaries display one path per row, showing four rows initially. Long parent paths shorten in the middle while the final folder name stays visible; hover for the full path. A chevron/Show more button appears only above four paths. Each list remembers its own expanded state for the current settings page. Manage still opens the editable path dialog.

Right-click a search result for Open, Show in Explorer and Open with. Open with uses the standard Windows [SHOpenWithDialog API](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shopenwithdialog) and is disabled for folders. File actions resolve the indexed entry and check its current scope/exclusions; a stale ID/path combination is rejected. The picker may move focus away, dismissing the ephemeral search bar as intended.

Successful Open/Open with actions remember the file for the exact normalized query. Repeat queries rank the most often opened file first, with the latest opening breaking count ties, then normal relevance. Query matching ignores case and repeated whitespace. Enter submits the query, including before results arrive, and never opens a document automatically. Opening requires a result click or an explicit context-menu action. Remembered candidates still obey included roots, exclusions, file-type/date filters and existence checks. Stored choices can bring an eligible file back even when its current text no longer matches the query. Files are identified by path; renaming or moving a file loses that remembered association. Show in Explorer, copy and canceled Open with do not increase open counts.

Queries and choices share account-encrypted `data/history.dpapi`. Legacy encrypted query arrays remain readable and migrate on the next write. History keeps at most 20 queries and 20 chosen paths per query. Removing a query also forgets its file choices; clearing history forgets all choices. Blank invocation still shows no history until typing. Lexical completion starts the stable-query save timer before slow semantic work finishes. Opening saves immediately; pending or failed actions do not record a successful file opening.

Run 020 checks real native encryption, old-history migration, persistence, count/recency ordering, removal/reset, scope-safe personalization and cache changes. Native WebView checks cover rendered folder rows, expansion, context actions, matching exact history and preferred ranking after browser disposal. Test action launches are intercepted, so no external document apps start during these integration checks; the Windows Open with picker itself was not visually tested. Screenshots, original profile backups and reports are preserved under `results/020_20261002T135646_history_actions_scope/`. Repeat with `node tests/check_frontend.cjs`, `.venv/Scripts/python.exe tests/check_personal_ranking.py --output results/NEW_RUN/ranking`, or a fixture profile using `AI Center.exe --check-history --data <new directory>` and `--check-search-ux --config <fixture config> --data <new directory>`. The latter expects five existing included paths, five exclusions, and `efs.txt` / `chosen.md` in the first root.

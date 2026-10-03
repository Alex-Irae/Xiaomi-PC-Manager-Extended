# Screen Translator

Current connected build: the native app loads the controls browser only when opened. Translation uses the bundled Marian ONNX graphs directly through OpenVINO with NumPy greedy decoding and the existing tokenizer. This avoids importing Torch for inference. The selected data profile keeps compiled model graphs, hardware validation and Python bytecode on disk; idle release still terminates inference workers after ten seconds. Controls navigation errors leave pending model loads intact. Cached loads measured 7.3 seconds on the tested laptop, with 22 exact decoder comparisons passing. These timings are hardware-specific. See the parent README for the complete offline EXE installer and current data paths.

This component is part of Xiaomi Revamp 0.2.0. Its released source, shared shortcuts, data paths and installer are documented in the [parent PC Manager README](../../README.md). Use the app's `Development/Launch.cmd` to rebuild and run edits with copied offline dependencies. The remaining notes document the standalone baseline and its original developer workflow.

This is the connected edition. The [parent README](../../README.md) provides current install/build commands and isolated data paths. Launch `install/Screen Translator/ScreenTranslator.exe`, or use `--toggle`, `--region` or `--tray`. Its main shortcut synchronizes with PC Manager; the hub also exposes screen/region/original/filter shortcuts and owns those bindings while running. Without PC Manager, the translator owns its own shortcuts. Settings includes optional shared appearance and Open PC Manager. The component documentation below retains baseline behavior and historical paths; its original setup/build scripts are not the current installer.

The included model supports **Chinese to English only**. Translation now requires a Chinese-only OCR box with confidence at least 0.85. English, other scripts, mixed-language boxes, numbers/symbols without Chinese and uncertain OCR remain untouched, even if an old text cache contains a translation. Recognition graphs use FP32 because GPU FP16 incorrectly recognized clear English labels as high-confidence Chinese on the tested Intel GPU. Changing precision refreshes the saved hardware choice once and can make the first load slower; later loads reuse compiled graphs on SSD. Mixed boxes are preserved because one OCR polygon cannot mask only the Chinese glyphs safely. This conservative rule can miss legitimate mixed-language labels; Han-only Japanese text and confident OCR mistakes remain indistinguishable by script alone. English-to-Chinese needs a separate compatible model and is not offered by this bundle.

Current connected behavior: settings uses a preset list with **Press a shortcut** last, opening the shared physical-key recorder. Assigning a key already used by a connected/custom action swaps their keys automatically. Controls and tray startup leave models unloaded. The screen shortcut immediately shows the toolbox in the upper-right corner with controls hidden, before loading models or waiting for OCR. This also applies when models are already loaded. Direct translation actions queue during model loading and continue when ready. Inference processes unload after ten idle seconds, or immediately on Stop; compiled OCR/translation caches remain under the selected profile. Cached restart still incurs Python imports and hardware warm-up and can exceed ten seconds. These rules supersede the historical resident/startup and conflict behavior described below.

**Development-only checkout.** The latest source, working `models/zh-en` bundle, settings, tests and build scripts are retained. Packaged EXEs/installers, duplicate saved/candidate models, old test results and generated native builds were removed during cleanup. Historical artifact/result paths below describe previous validation and are no longer present. No model or source change was made by the cleanup.

Double-click **Launch Development.cmd**, or use the development command under Launch. It rebuilds the native DLL with the existing SDK and runs the Python backend. Python packages, WebView2 and SDK installations remain outside this checkout. `data/config.json`, your appearance and the working model/calibration caches are preserved.

A standalone Windows 11 application that recognizes Simplified Chinese on a screen or selected region, translates it into English locally, and replaces it at its source position. The overlay is click-through. Existing Latin-only regions are preserved. This generic working name is independent of Xiaomi, PC Manager, AI Center and Click'n'Translate.

The frontend is **C# WinForms + WebView2**, with local HTML/CSS/JavaScript. Python owns the resident OpenVINO inference backend. The packaged EXE includes private Python/.NET runtimes and the working models. The previous Qt frontend is preserved for comparison. Packaged use does not depend on the reference apps or development tools.

## Install, startup and uninstall

The build produces `artifacts/NNN_timestamp_seed0/ScreenTranslator-Setup.exe`, `payload.zip` and `payload.sha256`. Keep these three files together and run Setup. Choose an empty dedicated folder; the default is `C:\Program Files\Screen Translator` and requires Windows administrator approval. Another writable dedicated folder works without elevation. The payload is approximately 1.18 GB compressed and 2.16 GB installed. Reserve at least 3 GB at the destination plus installer/cache space. The existing WebView2 Runtime, Visual C++ runtime and compatible Intel graphics/NPU drivers are still required. Setup does not download or install dependencies.

Open from the Start menu or `ScreenTranslator.exe`. **Copilot** toggles the live filter by default; **Esc** dismisses it. Settings accepts any available single virtual key or a combination with Ctrl, Alt, Shift or Win, such as `A`, `Ctrl+Shift+T`, or `F8`. Record key captures the physical chord in a native dialog. Save settings activates it globally; Windows security/reserved keys or conflicts are rejected while the previous binding remains active. The old default F8 migrates once to Copilot, while other saved bindings are preserved. Closing controls hides to the tray; tray Exit unloads and exits. A second launch opens the resident instance. At sign-in, startup loads models in the tray without capturing the desktop. This is a current-user desktop agent, because capture and overlays need the signed-in desktop. Startup can be disabled in Settings.

The Copilot preset listens for `Win+Shift+F23`. Some keyboards emit `Win+C` instead, so use Record key on that keyboard; both signals have a native listener that suppresses the chord and key repeat. Windows reports the emitted chord, not the identity of the physical key. Other keys emitting the same chord also invoke translation. [Microsoft documents these Copilot key variants](https://learn.microsoft.com/en-us/windows/powertoys/keyboard-manager). This is not a Windows-wide key remapping installation. No key history is saved.

Uninstall through Windows Settings > Apps or `Uninstall.exe`. Only owned package files and current-user integration are removed. Unknown files and user settings/logs/pictures/caches are retained. Uninstall an existing installation before reinstalling in the same folder. The installer is unsigned and has not been published.

Packaged settings, logs, WebView state, calibration and compiled caches live in `%LOCALAPPDATA%\ScreenTranslator`, outside Program Files. Models remain in the install folder. `--data-dir C:\path` overrides storage; `--tray` starts hidden; `--no-startup` prevents startup registration for checks. The app can also run directly from the build's `app` folder for portable evaluation.

Build uses already-installed packages/runtimes, WebView2 reference DLLs and Windows' .NET Framework compiler. It creates no environment and installs no packages:

```powershell
python tools/build_app.py --sdk 'C:\path\dotnet.exe' --webview 'C:\path\WebView2'
python tests/packaged_startup_check.py --app 'artifacts\NNN_timestamp_seed0\app' --seconds 10
```

The setup payload is separate because the offline models and runtimes are large. Its checksum and per-file ownership manifest are verified during installation.

## Appearance and power profiles

Settings provides Windows/light/dark themes, an accent colour, and a local PNG/JPEG/BMP picture. A decoded 256-pixel copy is stored in the user profile. Reset restores the existing translation glyph with a black background and white strokes, also used for the EXE, tray and installer icon. Theme/accent changes affect the native toolbar and translation underline. Controls use Windows DWM for rounded corners and minimize/restore transitions, preserving the same WebView across transitions. Tab and button transitions respect Windows' reduced-motion preference. The translation layer itself is never faded or animated away from its source text.

Translated text has an installed-font selector, relative size from 50% to 250%, optional shrinking to fit, and three display styles: accent underline (the unchanged default), plain replacement, or white text on a dark background at the source position. Enlarged/custom text expands into flat nearby whitespace only, stopping before other detected text or existing replacements. There is no general page reflow: crowded regions can still shorten text with an ellipsis and amber indicator. Turning fitting off keeps the requested font size within those geometric constraints. The Settings preview shows font/size/style; it does not predict a particular screen's available space.

**Save PNG** in the floating toolbar saves the active translated screen or selected region to `Pictures\Screen Translator` by default. Settings changes the absolute output folder. Exports contain the current original pixels composited with the native translated layer, without the floating toolbar. Original mode and a fully obsolete/empty layer disable saving. Partially changed areas are exported as original until their current translation arrives. If capture exclusion fails, the app temporarily hides its own surfaces while capturing. PNGs are saved only on an explicit action, with a timestamp and no overwriting; uninstall retains them. Saving can briefly block the UI during full-resolution PNG encoding.

**Auto** follows Windows' exposed power mode, with a battery fallback when unavailable. **Performance** uses the configured refresh interval; **Eco** uses at least 1500 ms and a slower content probe. Every profile uses validated automatic device selection. These are cadence policies rather than an unverified model switch. An OEM fan/quiet setting is detected only if it also changes Windows power mode.

With a translation target active, a small in-memory probe checks content about every 100 ms, or 400 ms in Eco, independently of toolbar focus. A 16 by 10 grid localizes changes: affected rendered words clear in full, including any expanded whitespace, while unchanged translations remain visible. Window title changes clear the entire old layer immediately. Refresh waits for 160 ms of settled content, or 320 ms in Eco, subject to the configured minimum interval between capture starts. Sustained changes allow a new frame after at most the greater of 1000 ms or the configured interval, provided the worker is available. This avoids repeatedly sending obsolete frames during short scroll bursts without starving unrelated text when a corner animates. The 100 ms visibility timer schedules eligible refreshes directly instead of waiting for another full filter-timer period. In-flight results are masked against all changes observed since their capture; they cannot restore affected old words. Unchanged targets skip full capture, PNG transport and inference. With no target, both polling timers stop and resident models sleep.

Moving text still clears during continuous scrolling and returns after recognition; cached translations do not track the scroll position at display refresh rate. The probe is deliberately coarse, so tiny changes below its threshold may need explicit translation. More expensive OCR or a full-screen change can still take longer than the settling delay. No optical-flow guesses or unverified text-position shifts are applied.

**Status:** the model bundle is provisioned with verified hashes. Actual user runs exposed slow full-screen translation, missed Chinese and text with its bottom half cut off. The follow-up repair fixes native DPI painting and preserves small glyphs with overlapping detection tiles and separate fixed short-label recognition. Twelve regressions pass. A generated 3120 x 2080 screen with 18/22/26-pixel text on light and dark backgrounds recognizes all 66 Chinese strings exactly and preserves all 66 English regions. Actual overlay painting matches its physical-pixel canvas at 100%, 150%, 200% and 250% scaling. General translation quality and the specification's 0.5–2 second live hotkey target have not been established.

## Program flow

`Hotkey -> C# capture -> JSON stdio -> Python supervisor -> authenticated local engine pipe -> changed tiles -> OCR polygons -> text cache/NMT -> C# replacement overlay`

Models remain resident until Unload models, a settings reload or Exit. Screen captures and recognized/translated text remain in RAM unless you explicitly save a PNG. There is no HTTP server, account, telemetry or cloud translation path. Explicit model provisioning is the only download operation.

## Environment and hardware

- Windows 11 x64, Python 3.12 and the root `requirements.txt` dependencies.
- An existing .NET 8+ SDK with .NET 8 Windows Desktop reference/runtime packs, installed WebView2 Runtime, and local WebView2 SDK assemblies/loader.
- OpenVINO 2026 series, with Intel GPU/NPU drivers for acceleration.
- Development hardware: Intel Core Ultra X7 358H, Intel Arc B390 iGPU, Intel NPU and 32 GB RAM.

NPU/iGPU acceleration is preferred when its measured inference is faster; CPU is the explicit reported fallback for a stage if it wins the comparison or the accelerators cannot run that graph. Capture, image preprocessing, tokenization, generation control and layout still use the CPU. Device Manager presence alone does not establish model compatibility.

This package is **Windows 11 x64 with Intel hardware**, tested on the machine above, not universal hardware support. The inference backend can select CPU for every stage when accelerators are unavailable, but this application's complete CPU-only workflow has not been qualified on other laptops. OpenVINO's bundled GPU and NPU plugins target supported Intel devices; AMD/NVIDIA GPU and Qualcomm NPU acceleration, ARM64 Windows, macOS and Linux application builds are not included. An AMD x64 CPU may work via the CPU plugin but is untested here and outside Intel's officially supported platform guarantee. See [OpenVINO system requirements](https://docs.openvino.ai/2026/about-openvino/release-notes-openvino/system-requirements.html) and [Intel's AMD support statement](https://www.intel.com/content/www/us/en/support/articles/000093369/software/development-software.html). A sufficiently fast chip alone does not establish compatibility: architecture, supported instructions, drivers and runtime plugins also matter.

Environment creation, package installation and driver updates remain user actions:

```powershell
Set-Location 'C:\Users\Irae\Documents\Documents\Xiaomi Rebuild\screen-translator'
python -m pip install -r requirements.txt
```

The local WebView2 SDK directory must contain `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, and `WebView2Loader.dll` or `runtimes/win-x64/native/WebView2Loader.dll`. No npm or remote NuGet package installation is required. `native/NuGet.Config` clears remote feeds.

## Launch

Portable development command, with your existing dependency paths:

```powershell
pwsh -NoProfile -File ./dev.ps1 -Sdk 'C:\path\dotnet.exe' -WebViewReferenceDir 'C:\path\WebView2' -Python 'C:\path\python.exe'
# UI editing without model initialization:
pwsh -NoProfile -File ./dev.ps1 -Sdk 'C:\path\dotnet.exe' -WebViewReferenceDir 'C:\path\WebView2' -NoLoad
```

On this workstation, `Launch Development.cmd` contains this command:

```powershell
pwsh -NoProfile -File ./dev.ps1 -Sdk 'C:\Utils\XiaomiPc Manager\development\XiaomiAIManager\.test-environment\dotnet\dotnet.exe' -WebViewReferenceDir 'C:\Utils\XiaomiPc Manager\development\XiaomiAIManager\app' -Python 'C:\Program Files\Python312\python.exe'
```

These external paths supply development dependencies only. To remove those reference-app folders, keep your SDK/runtime and WebView2 SDK in an independent location and pass those paths. The translator itself has no dependency on their application code or installation.

`python -m screen_translator` starts the preserved Qt comparison frontend. Its older device controls/rendering are separate from the current C# workflow. CLI experiments still accept explicit device IDs; desktop users do not select them.

## Models and automatic hardware

The provisioned `models/zh-en/manifest.json` records actual SHA256 checksums, model provenance and preprocessing. Every local model/tokenizer asset is validated before loading. A fresh copy can explicitly provision its bundle:

```powershell
python -m screen_translator.provision
python -m screen_translator.cli inventory --models models/zh-en
```

The **Verify / restore bundled models** button calls this same fixed provisioning path. The packaged working model is already present: matching files pass checksum verification and are reused with no network download. Only missing files are fetched from pinned Hugging Face revisions, about 687 MB for an empty bundle. These twelve assets include OCR detection/recognition, OPUS encoder/decoder graphs and tokenizer metadata, not an engine installer, marketplace or automatic model upgrade. Interrupted downloads resume where supported. Conflicting completed assets are preserved and reported rather than overwritten; choose a new writable model bundle directory for changed/corrupt files. Restoring missing files under Program Files may require choosing a writable bundle folder. Verification stops the current layer and reloads models afterward. Package installation and model provisioning are different steps.

Current models:

- OCR: PP-OCRv4 Chinese from [SWHL/RapidOCR](https://huggingface.co/SWHL/RapidOCR), revision `1cfba2e90fc938db55889873735088de210cc173`, Apache-2.0.
- Translation: [Helsinki-NLP opus-mt-zh-en](https://huggingface.co/Helsinki-NLP/opus-mt-zh-en), [Xenova ONNX export](https://huggingface.co/Xenova/opus-mt-zh-en), revision `39d480d52a9ea3065a1f117adfe4dbc55de10e6f`, CC-BY-4.0.

The native frontend uses `Managed` automatic selection. On first load, it compares available OpenVINO devices separately for detection, recognition and translation. Each candidate receives one warm-up and three timed repetitions. Detection uses generated Chinese/English glyphs and must agree with a CPU reference foreground mask (IoU >= 0.90); recognition uses a fixed generated tensor; translation uses four public Chinese sentences. It selects the lowest median successful device for each stage and records actual execution devices. Unsupported candidates are reported. These measurements rank this workload, not live screen latency or translation quality. Warm-up and initial calibration can take substantially longer than later loads.

CLI selection state is stored in `models/zh-en/model_cache/native-device-choice.json`. Native development and installed builds store it under their data directory, keyed by model hash. The package can seed the already measured choice only when its complete hardware/model signature still matches. Its identity includes the policy version, model manifest hash, OpenVINO/OS/device/driver fingerprint and batch size. A matching load reuses the selected devices and verifies warm inference; a failed cached stage is measured again. Calibration evidence is preserved under numbered `results/automatic/` directories with full configuration, timings/failures and `latency.svg`. That figure compares warm stage milliseconds, lower is faster, and excludes capture/rendering.

The native detector processes overlapping 1280-pixel tiles with 192-pixel overlap at the original physical resolution. A 3120 x 2080 screen uses six tiles instead of shrinking the whole screen to 1280 pixels. Cross-tile duplicates are removed and overlapping horizontal seam fragments are joined before recognition from the full original image. Separate detections within one tile are never joined by this step. This preserves small glyphs at the cost of more detection work.

Recognition compiles two fixed shapes once: `[8,3,48,320]` for short labels whose content width at height 48 is at most 160 pixels, and `[8,3,48,1024]` for longer lines. This avoids dynamic-width graph specialization and reduces blank padding on short labels. Overwide crops are split into bounded strips and mapped back to source quadrilaterals. GPU detection explicitly requests FP32: the prior FP16 hint produced misplaced boxes and blank source crops on this driver. GPU recognition retains FP16 and passed the public fixtures. Accelerator top-1 reduces `[B,T,C]` scores to winning IDs/confidences `[B,T]` before host transfer. RapidOCR supplies CTC decoding and detector postprocessing, not its ONNX Runtime inference pipeline. The embedded dictionary is checked against the recognizer's original class count.

Translation uses deterministic greedy NMT, batches of up to eight distinct cache misses, a 512 source-token limit and 256 new-token limit. Incomplete generation raises an error. Exact common UI labels such as 设置, 确认 and 取消 use a small local glossary to avoid misleading context-free paraphrases. Other strings still use the same NMT model. This glossary does not establish general translation quality.

**Batch size** means the number of text units processed together. It affects throughput/memory, not semantic quality. The native frontend manages it internally at eight and exposes no hardware or batch selectors.

Yandex's [web image translator](https://yandex.ru/support/translate-desktop/en/phototranslate-desktop) is a visual/quality reference. Its [mobile offline documentation](https://yandex.com/support/translate-app-android/en/offline-mobile) describes downloaded dictionaries for typed text. No supported offline Windows model export was identified in those public documents. This application does not use Yandex's cloud backend.

## Controls and rendering

| Action | Shortcut |
| --- | --- |
| Translate selected monitor | Ctrl + Alt + F |
| Select region | Ctrl + Alt + T |
| Original / translated view | Ctrl + Alt + O |
| Translation filter on/off | Ctrl + Alt + D |
| Dismiss translation and filter | Esc, while a translation target is active |

A compact rounded native toolbar provides Original/Translated, Filter and Dismiss, with a live/snapshot status and blue active-filter button. Drag its title to reposition it. Refresh retains its position and does not activate the toolbar. Translation patches remain click-through; the toolbar's buttons are interactive. Escape removes the overlay immediately, stops the filter and leaves models resident. A pending inference may finish in the background, but its result cannot restore a dismissed overlay. Escape is unregistered when idle so ordinary application use is unaffected.

Settings includes **Show translation only while the toolbar is active**, now disabled by default for continuous use. Enabled: the original returns when another window becomes active, but background refresh continues without requiring a click on the toolbar. Disabled: translation stays visible while using other applications. Older configurations migrate once to the new default, with `refresh_version: 2`; explicitly enabling focus-only visibility after migration persists. A 100 ms timer applies visibility and schedules the small content probe while a target is active. It stops when no target is active. The overlay and toolbar use native topmost styles rather than WinForms' managed `TopMost`, whose show path restores keyboard focus. Normal refresh keeps the overlay visible during canvas replacement; an identical cached frame skips replacement unless a previously masked canvas needs restoration. Capture exclusion still needs to succeed for continuous filtering.

Show original hides the replacement; Show translation restores its existing canvas immediately. It does not rerun OCR merely to switch views. Original view pauses filtering. If the screen changes while Original is shown, the probe marks its cached translation stale. Resuming requests a new result instead of showing the obsolete layer. Filter operates on the most recent target and skips busy ticks. Opening controls pauses refresh while the window is visible, retaining the filter switch. Closing or minimizing controls resumes refresh; a minimized controls window no longer blocks page probing or replacement visibility.

Only translated regions receive a subtle underline in the selected accent colour. Latin-only regions stay unchanged. Amber underline means text cannot fit in its source bounds and may be ellipsized. Text fitting first uses adjacent flat whitespace to the right, up to four source widths, stopping before any recognized neighbour or non-background pixel. It tries single-line and wrapped layout at decreasing font sizes, down to a bounded minimum; single words never wrap. No translation cards are drawn. Perimeter median colour approximates flat backgrounds; texture reconstruction is not implemented.

Physical capture coordinates use PerMonitorV2 awareness, including negative monitor origins. Overlay painting explicitly specifies pixel source and destination rectangles, so its bitmap stays aligned with the native region mask at high DPI. The previous two-argument image draw enlarged the canvas at 200% scaling, causing the mask to cut off text. Windows display affinity is verified for the overlay and toolbar; failed overlay exclusion disables continuous filtering to avoid OCR of its own output. Displays changing clear the target. GDI capture can omit protected surfaces. Close hides controls to the tray; tray Exit unloads the owned backend process tree. **Unload models**, previously labelled Stop inference, terminates the resident inference worker, releases its model/device allocations and caches, stops filtering and clears the overlay. The controls/supervisor remain available. Load models starts a fresh worker. Changing display/focus settings no longer reloads models; changing the model path or pipeline cache settings still does.

## Source structure and data

| Path | Responsibility |
| --- | --- |
| `frontend/index.html`, `app.css`, `app.js` | Offline WebView2 controls/settings/diagnostics |
| `native/Program.cs` | Native bridge, capture, shortcuts, tray, lifetime and view state |
| `native/DesktopOptions.cs` | Local appearance, startup registration, single-key shortcut, power mode and stale-page probe |
| `packaging/`, `tools/build_app.py` | Existing-logo ICO conversion, private-runtime launcher, selectable-folder installer and owned-file uninstaller |
| `native/Surfaces.cs` | Selection, replacement geometry/text fitting, click-through overlay and toolbar |
| `screen_translator/backend.py`, `engine.py` | JSON supervisor and crash-isolated resident inference process |
| `screen_translator/hardware.py` | Device inventory, automatic stage measurements and legacy calibration |
| `screen_translator/ocr.py`, `runtime.py` | OpenVINO OCR/translation, CTC reduction and exact-label glossary |
| `screen_translator/core.py`, `change.py`, `reading_order.py` | Geometry, Chinese-only fragment grouping, bounded transactional caches and dirty tiles |
| `screen_translator/models.py`, `provision.py`, `model-catalog.json` | Integrity validation and explicit pinned provisioning |
| `tests/` | User-run regressions, native startup checks and generated translation fixture |
| `tests/provision_candidates.py`, `tests/model_efficiency_check.py` | Separate pinned candidate downloads and generated latency/package-energy comparisons |
| `dev.ps1`, `native/ScreenTranslator.csproj` | Existing SDK compilation and source-development launch |

`data/config.json` in development, or `%LOCALAPPDATA%/ScreenTranslator/config.json` when packaged, stores model path, cache/incremental switches, interval/font settings, focus policy, theme/accent/picture, shortcut, startup and power profile. Loading an older native config migrates hardware mode to Managed and batch size to eight. `data/native.log` records stages/errors, `data/timings.jsonl` records counts/stage times, and `data/webview/` stores the local WebView profile. Large logs are renamed, not discarded. No normal screenshot/text history is written.

The supervisor starts its worker with disconnected stdin and a randomly named authenticated Windows AF_PIPE connection. Worker stdout/stderr go to the diagnostic pipe rather than corrupting JSON responses. PNG transport adds CPU/encoding cost included in native hotkey timing. The system C++ runtime is preferred before native inference imports, avoiding the previously observed bundled-runtime access violation. No system DLL is replaced.

`models/zh-en/model_cache/` is generated state exempt from the sealed asset list. `results/` stores explicit check/calibration evidence in new numbered directories; prior results are never overwritten. Generated fixture results may store public fixture text/images. Ordinary desktop operation does not save them. `NOTICE.md` attributes the two locally retained GPL helper modules; `LICENSE` remains GPL-3.0.

## Validation commands and evidence limits

Repository policy requires explicit authorization before executing these tests or application scripts:

```powershell
python -m unittest discover -s tests -p 'test_*.py' -v
python tests/generated_translation_check.py
python tests/generated_translation_check.py --dense
pwsh -NoProfile -File ./dev.ps1 -Sdk 'C:\path\dotnet.exe' -WebViewReferenceDir 'C:\path\WebView2' -CheckUi
# Render only the explicitly generated fixture with the native overlay:
& 'C:\path\dotnet.exe' ./native/bin/Debug/net8.0-windows/ScreenTranslator.dll --root $PWD --check-ui --fixture-directory 'C:\path\results\generated\NNN_timestamp_seed0'
```

Regressions cover geometry/cache transactionality, device-property decoding, mixed-language grouping, exact-label translation, model corruption, local bridge boundaries and engine recovery. The native check inspects actual DOM state, view controls during inference, replacement geometry/capture exclusion, Original without recapture, Filter disengagement and Escape dismissal of pending results. It uses generated content, never desktop capture. It writes `results/frontend/NNN_timestamp_seed0/` with configuration, summary and a preview of its own frontend, optionally a native-rendered generated fixture.

The generated fixture creates 3120 x 2080 flat mixed-language content with Microsoft YaHei at 40 pixels by default. `--dense` uses three columns of 18/22/26-pixel text, including a dark-background column, with 66 Chinese and 66 English regions. Both modes run actual selected OCR/NMT devices. Three uncached pipeline passes, a cached repeat, geometry-matched exact OCR checks, short-label translations and untouched Latin regions are reported under `results/generated/`. Its `latency.svg` shows pipeline milliseconds per pass; capture, transport, native layout and hotkey scheduling are excluded. Sentence outputs remain available for human semantic review. Even the small-text fixture uses clean flat backgrounds, so passing is necessary evidence, not general quality acceptance.

Historical evidence is preserved: nine native/backend regressions passed before this repair; `results/frontend/003_20261002T0825549153163Z_seed0/` passed generated geometry/UI checks at 192 DPI. Earlier native startup reached readiness in about 47 seconds. A previous Qt recovery check reached readiness in 38.08 seconds. These are startup observations, not latency or accuracy benchmarks. The <5 second initialization target remains unmet.

## Known limitations

- Current NMT is a lightweight model with limited UI/context quality. The glossary repairs known short labels only. Better offline model candidates require separately validated local exports and quality/latency comparisons.
- Detection no longer downsamples an entire native screen, but glyphs that are already tiny, blurred or low contrast can still be missed or misrecognized. Vertical/rotated text, handwriting, textured backgrounds and dense prose remain unvalidated.
- Long-line strips can split a glyph at a boundary. Conservative same-line grouping can merge adjacent Chinese controls. Mixed-script and low-confidence boxes are preserved unchanged under the current eligibility rule.
- Narrow source rectangles with no safe adjacent whitespace can force small fonts or ellipses. Amber underline reports overflow; it does not recover all missing words.
- Device calibration on generated tensors ranks a small workload. Driver load, thermal limits and other applications can change which device is fastest.
- The native paint path passes generated pixel-equality checks at four DPI values. Actual multi-monitor transitions, real click-through, scrolling/filter behavior and full hotkey-to-useful-translation quality still need acceptance checks. Startup tests alone cannot establish them.
- Windows Graphics Capture, shared-memory transport and texture inpainting are not implemented. One monitor or selected region is active at a time.
- Package-energy measurements include background applications, are not whole-laptop power, and cannot establish battery life. Generated fixtures do not establish unrestricted live OCR/NMT quality.

## October 2 repair evidence

- `results/generated/001_20261002T091524Z_seed0/` and `002_20261002T091633Z_seed0/` preserve failed CTC graph rewiring checks. A small real OpenVINO regression now verifies winning IDs/confidences against NumPy argmax/max.
- `results/generated/003_20261002T091738Z_seed0/` preserves the failed GPU FP16 detector result: only 20 recognized regions despite 47 detector boxes, many sampling blank areas. `results/ocr-diagnosis/001_20261002T092157Z_seed0/crops.png` shows those generated crops. Changing recognition to CPU did not fix the bad boxes. CPU/NPU detection and GPU FP32 detection recovered all 44 intended regions.
- `results/automatic/002_20261002T092558Z_seed0/` selected GPU detection, GPU recognition and CPU translation. Warm measured stage medians were 22.2 / 7.7 / 71.9 ms respectively. NPU detection/recognition were valid but slower for these workloads; NPU autoregressive translation failed dynamic reshape compilation. These measurements include validation overhead for detection and are not live end-to-end timings. The earlier native-v3 calibration is invalid for quality and superseded by the cache policy.
- `results/generated/004_20261002T092458Z_seed0/` passes exact Chinese OCR, the 18 glossary labels, and all 22 unchanged English regions. Uncached pipeline passes were 265.6, 224.0 and 781.1 ms, median 265.6 ms; identical-frame cache took 39.4 ms. The observed variation is preserved. These are generated large-font results, not a controlled before/after comparison with the user's screen.
- `results/frontend/008_20261002T0936124293404Z_seed0/` passes responsive view controls, both filter transitions, immediate Escape dismissal and stale-result suppression at 192 DPI. The native generated render has 22 replacements, zero clipping and no mask intersection with the original English regions. `translated-fixture.png` previews the actual C# renderer.
- `results/decoding/002_20261002T093421Z_seed0/` compares greedy and four-beam decoding on public text. Four beams took 822 ms versus 288 ms for greedy, improved one short sentence and worsened an academic label. Greedy remains the default. Outputs preserve examples of the model's awkward wording and technical mistranslations; the glossary does not hide that general limitation.

All figures and screenshots above contain public generated content only. The preceding DPI/OCR repairs used existing models. The subsequent candidate comparison below explicitly downloads separate models at the user's request. Those earlier repairs performed no desktop capture, package installation or Git command. The later packaging work is documented above.

Earlier native bridge startup passed in 18.19 seconds with cached GPU/GPU/CPU choices, recorded in `results/native-startup/001_20261002T093707Z_seed0/`. This check initialized models only and captured no screen.

### Follow-up for chopped glyphs and missed small text

- The prior `results/frontend/008_20261002T0936124293404Z_seed0/` PNG check exercised composition, not native window painting. It therefore missed the actual DPI bug. `results/frontend/010_20261002T0958393676121Z_seed0/` reproduces the paint mismatch at 150%, 200% and 250%. After explicit pixel painting, `011_20261002T0959178564028Z_seed0/` and `012_20261002T1014048963163Z_seed0/` have zero differing pixels at all four DPI values.
- `results/generated/005_20261002T100335Z_seed0/` preserves the dense fixture's failed whole-screen downsampling result: 63/66 exact Chinese strings. Native-resolution tiles improved this to 65/66 in `006_20261002T100719Z_seed0/`. Separate short-label recognition recovered the final 18-pixel label in `007_20261002T101023Z_seed0/`: 66/66 exact Chinese strings, all 54 glossary labels correct, and all 66 English regions unchanged.
- The dense uncached pipeline took 709.3, 685.2 and 1179.5 ms, median 709.3 ms; its identical-frame cache took 28.8 ms. This includes detection, recognition and translation, but excludes native capture/transport/rendering. More detection work is a deliberate tradeoff for small-text coverage. These timings are not evidence that a user's real screen now translates within one second.
- The native dense render in `results/frontend/012_20261002T1014048963163Z_seed0/` has 66 replacement regions, zero reported text-fit clipping and no mask intersection with the English regions. Escape, stale-result suppression, Original and both Filter transitions pass. `translated-fixture.png` shows the C# renderer on the public fixture.
- The original large-text fixture still passes after the follow-up changes in `results/generated/008_20261002T101404Z_seed0/`: 22/22 exact Chinese strings and 22 unchanged English regions; uncached median 514.8 ms, identical-frame cache 29.2 ms. All 12 backend regressions pass. Translation beyond the exact-label glossary still uses the same NMT model and retains its documented semantic limitations.
- Follow-up native bridge startup passes in 18.20 seconds in `results/native-startup/003_20261002T1018107497154Z_seed0/`, including the additional short-label graph, with GPU/GPU/CPU execution verified. The repaired development app was left running with models ready. No screen was captured. `002_20261002T1017067789066Z_seed0/` preserves a failed reporting attempt that assumed a saved settings file existed; the application itself supports absent settings through defaults and was not started by that failed attempt.

## Preserved baseline and candidate comparison

The user-confirmed working bundle is preserved in `models/saved/working-20261002T102639Z/bundle/`. Each asset was copied and verified against the sealed manifest. The adjacent `source/` preserves the application source and launch files at that point, outside the native compiler's source tree. `snapshot.json` identifies the baseline. Normal development still uses `models/zh-en/`; no candidate is automatically promoted.

Two separate candidates are provisioned:

- `models/candidates/ppocr-v5/`: PP-OCRv5 mobile detection/recognition and its exact dictionary, [WebNN ONNX export](https://huggingface.co/webnn/PP-OCRv5-ONNX), pinned revision `006b4419a415072fbb16a1d7d0d8548f789a8fae`. It reuses the working translation assets. Upstream [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) is Apache-2.0. This exported OCR model is a genuine alternative architecture; quality beyond the generated fixture is unvalidated.
- `models/candidates/opus-int8/`: upstream dynamic-INT8 ONNX variants from the same pinned [Xenova OPUS export](https://huggingface.co/Xenova/opus-mt-zh-en), with the working OCR/tokenizer. This is a quantized variant of the same translation model, not a new language model or a semantic-quality upgrade.

The downloader verifies pinned LFS SHA256 or Git blob IDs, copies unchanged sealed assets and creates separate sealed manifests. Partial downloads remain resumable. Model downloads are authorized network operations; application inference and candidate checks remain local.

```powershell
python tests/provision_candidates.py --variant all
python tests/model_efficiency_check.py --bundle models/zh-en --stage ocr --seconds 6
python tests/model_efficiency_check.py --bundle models/candidates/ppocr-v5 --stage ocr --seconds 6
python tests/model_efficiency_check.py --bundle models/zh-en --stage translation --seconds 6
python tests/model_efficiency_check.py --bundle models/candidates/opus-int8 --stage translation --seconds 6
# Diagnostic GPU precision override, without changing application defaults:
python tests/model_efficiency_check.py --bundle models/candidates/opus-int8 --stage translation --devices GPU --gpu-precision f32 --seconds 6
```

To test a candidate manually, choose its directory under Settings, Local model bundle. Restore `models/zh-en` to return to the working baseline. Hardware remains automatic. Unsupported or failed execution must not be mistaken for a successful accelerator result.

Each efficiency run writes a new `results/efficiency/NNN_timestamp_seed0/` with complete configuration/device identity, actual execution-device properties, source/output records, five warm timing samples, a separate sustained energy interval, CSV and `comparison.svg`. OCR uses the explicitly generated dense 66-Chinese/66-English screen. Translation uses four public Chinese sentences; output completeness is checked, while semantic correctness still requires review. Empty translation output is excluded from timing/energy ranking.

Package energy uses the Windows `Energy Meter(RAPL_Package0_PKG)` cumulative counter through PDH. The counter's own explanation must confirm picowatt-hours before measurement. Energy is $\Delta E_{pWh}\times3.6\times10^{-9}$ joules; divide by completed operations for joules/operation, or by interval seconds for average watts. Lower latency and joules/operation are desirable. This measures all processor-package activity, including preprocessing and background applications. It does not isolate NPU/GPU power, measure charger/display/whole-laptop consumption, or subtract idle power. Device compile/load energy is excluded. Thermal state, other applications and short sampling intervals limit comparisons; these are pilot measurements, not a definitive battery-life benchmark. [Microsoft EMI documentation](https://learn.microsoft.com/en-us/windows-hardware/drivers/powermeter/energy-meter-interface) describes cumulative picowatt-hour measurement.

After frontend checks finished, serial model comparisons produced:

| Stage / model | Device | Warm median ms | Package J / operation | Generated result |
| --- | --- | ---: | ---: | --- |
| OCR / working PP-OCRv4 | GPU | 520.1 | 11.91 | 66/66 exact Chinese |
| OCR / working PP-OCRv4 | NPU | 693.9 | 12.12 | 66/66 exact Chinese |
| OCR / working PP-OCRv4 | CPU | 1133.1 | 59.17 | 66/66 exact Chinese |
| OCR / PP-OCRv5 | GPU | 554.5 | 10.99 | 66/66 exact Chinese |
| OCR / PP-OCRv5 | NPU | 721.0 | 12.40 | 66/66 exact Chinese |
| OCR / PP-OCRv5 | CPU | 1199.1 | 65.01 | 66/66 exact Chinese |
| Translation / working FP32 assets | GPU | 108.3 | 4.91 | Four complete outputs |
| Translation / working FP32 assets | CPU | 64.3 | 3.07 | Four complete outputs |
| Translation / INT8 variant | CPU | 109.7 | 4.40 | Four complete outputs |

OCR operations are complete generated-screen OCR passes; translation operations are four-sentence batches. Their energy values are not directly comparable across stages. Reports are `004_20261002T104652Z_seed0/` through `007_20261002T104945Z_seed0/`. Both translation variants fail NPU compilation on a dynamic reshape. The INT8 GPU variant under the normal FP16 math hint returns empty outputs and is rejected. An explicit GPU FP32 retry also returns empty outputs in `008_20261002T105045Z_seed0/`, so simply changing the math hint does not repair this export on this driver. Earlier `001` through `003` are exploratory checks, some overlapping native UI work, and are not used for the serial comparison above. All negative evidence remains preserved.

The working GPU OCR / CPU translation choice remains active. PP-OCRv5's modest measured GPU energy difference needs a longer controlled comparison and real quality benefit before changing the default. Native `results/frontend/024_20261002T1057314847245Z_seed0/` passes generated refresh visibility, non-activating toolbar presentation, both focus policies, Original/Filter/Escape and four DPI paint checks. Earlier focus-check failures remain preserved; the stalled attempt produced no completed report. The actual fix uses native topmost window styles to avoid WinForms' focus restoration. Windows sometimes reports a zero foreground handle during transitions; only in that case, the policy falls back to this UI thread's active window. A known different foreground window always hides the overlay in focus-only mode.

The repaired development app was restarted with the unchanged working bundle and reached model readiness in 18.17 seconds in `results/native-startup/004_20261002T1058414143082Z_seed0/`. Execution is GPU detection/recognition and CPU translation. It was left running; no desktop was captured by the startup or validation checks. All 12 backend regressions pass, and the development build has no warnings/errors. The continuous filter still needs user acceptance for live flicker and focus behavior after these changes.



## Packaged app and broader comparison evidence

- `results/packaged/001_20261002T114956Z_seed0/`: the packaged EXE uses its private Python/.NET paths, reaches ready in 23.51 seconds, and verifies GPU.0 OCR plus CPU translation. A ten-second resident idle sample uses 0.172 CPU seconds across the process tree, 1.72% of one core. The measured 8.65 W is the entire processor package, including other applications. Summed process working sets are about 3.7 GB and include shared pages; they are not unique RAM ownership. Keeping models ready trades memory for immediate activation after warm-up.
- `results/installer/001_20261002T115208Z_seed0/`: real compiled setup/uninstaller on tiny fixtures verifies a chosen path containing spaces, rejection of nonempty targets, file ownership removal, preservation of an unknown file, traversal rejection and checksum rejection. No startup entries or Program Files installation were created by this fixture check.
- `results/frontend/026_20261002T1157076853327Z_seed0/`: Escape, focus modes, stale-page invalidation, probe threshold and Eco cadence pass on public generated content. Later checks cover final appearance changes. Real browser Ctrl+Tab acceptance is separate from these synthetic checks.
- `results/comparison/002_20261002T122612Z_seed0/REPORT.md` (or the newest numbered comparison folder): three OCR variants and FP32/INT8 translation were compared on a 72-line prose screen and 24 UI sentences. The working model stays selected; no corpus-specific glossary was added. Raw model outputs and failures are preserved.

PP-OCRv5 mobile improves exact prose lines from 57/72 to 61/72 on GPU in the current pipeline. The server candidate is not eligible for a definitive model-quality ranking: its export metadata specifies RGB detector input and a 32-pixel recognizer height, whereas the latter fails graph shape validation in results/efficiency/016_20261002T122404Z_seed0/. The prior server rows are only 48-pixel production-pipeline compatibility diagnostics. INT8 CPU translation is slower and uses more package energy; its GPU output is incomplete. FP32 and INT8 both make clear meaning errors in the public network-settings sentence. Neither result establishes general translation accuracy.

Auto/Performance/Eco therefore control polling/refresh cost while retaining the validated working model and automatic hardware. Switching model families automatically would require stronger quality acceptance and repeated battery measurements. Background downloading overlapped part of the OCR comparison, so small package-energy differences remain inconclusive. Models remain wholly local during use.


Final packaged native UI checks pass in `results/packaged-ui/001_final/results/frontend/001_20261002T1208218851299Z_seed0/`, including WebView theme/accent state, native toolbar/underline appearance, 66 generated replacements with no clipping, focus and Escape, stale content invalidation and Eco cadence. Final compiled installer fixture checks also pass in `results/installer/002_20261002T120818Z_seed0/`. These are synthetic checks and do not establish real-browser event timing.

The full payload check exposed legacy .NET Framework path normalization at a dependency path longer than 260 characters. Installer and uninstaller now explicitly target .NET Framework 4.8 and use long-path-aware manifests, without changing Windows policy. `results/installer/005_20261002T121735Z_seed0/` (or the latest numbered installer check) verifies deep-path extraction, integrity checks and scoped removal. The earlier failed/full-partial installs are preserved as negative evidence; no unrelated files were removed.

Final full-payload verification passes in `results/installed/002_20261002T122033Z_seed0/`: custom-location extraction and all file hashes, relocated EXE/private-runtime startup, unchanged installed assets, and actual uninstall preserving an unknown file. No Program Files or startup registration was touched. The relocated startup/idle report is `results/packaged/002_20261002T122111Z_seed0/`: 28.55 s readiness, 0.031 CPU seconds over ten idle seconds (0.31% of one core). Its whole-package 7.15 W includes background activity and is not the app's own draw. No desktop was captured.

## October 2: scroll refresh and window transitions

The preserved scroll-smoothing build is `artifacts/002_20261002T132805Z_seed0/`. It keeps the same sealed working model bundle. The frontend, EXE, tray and installer use the original glyph in black and white. Native frame styling follows [Microsoft's custom-frame guidance](https://learn.microsoft.com/en-us/windows/win32/dwm/customframe) and [Windows 11 corner guidance](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners); it replaces the main window's hard region clipping with DWM corners, retaining native minimize/restore styles.

Twelve backend regressions pass and the C# build has no warnings/errors. `results/packaged-ui/002_scroll/` verifies one-time migration from an older focus-only configuration. The final generated UI report in `results/packaged-ui/003_scroll_final/results/frontend/001_20261002T1338407025362Z_seed0/` verifies focus-independent refresh eligibility, selective whole-word invalidation, deduplication over twenty repeated change observations, masking of obsolete in-flight results, full-page stale-layer suppression, bounded scroll debounce, native minimize/restore, monochrome icon pixels, Escape, Original and Filter. All four DPI paint comparisons match exactly. The dense public fixture renders 66 translations with zero clipping and leaves English areas untouched; native layout takes 78.94 ms. A 100-iteration generated comparison of fully changed thumbnail pixels averages 0.213 ms, excluding GDI capture. This measures only the comparison step, not scrolling or end-to-end OCR.

`results/installer/006_20261002T133745Z_seed0/` passes the compiled custom-path, long-path, integrity and scoped uninstall fixtures. The first fresh bundle launch in `results/packaged/003_20261002T133405Z_seed0/` reaches readiness in 75.06 s; the second in `results/packaged/004_20261002T133850Z_seed0/` reaches readiness in 23.02 s. The first launch spends much longer importing the translation engine. Both use GPU OCR and CPU translation. The second idle sample consumes 0.109 CPU seconds over ten seconds across the owned process tree; its whole-package 5.56 W includes all background processes and is not an attributable app or battery measurement. These checks do not capture the desktop or register startup.

Actual browser/app scrolling smoothness, taskbar animation appearance and live page-switch timing remain user acceptance items. Moving source text is cleared until a current OCR result arrives; there is no claim of frame-rate scroll-following translation. The generated checks establish geometry and stale-content safety, not live screen latency or improved general model accuracy.

## October 2: text settings, Copilot shortcut and PNG export

The current build is `artifacts/003_20261002T145738Z_seed0/`, preserving the previous accepted package. No inference model or installed dependency version changed. The default 3120 x 2080 fixture composite is pixel-identical to the previous build's output.

`results/packaged-ui/005_20261002T150302Z_seed0/results/frontend/001_20261002T1503076898286Z_seed0/summary.json` passes editable settings during engine loading, arbitrary shortcut parsing, single-trigger Copilot chord/release handling and Windows shortcut-conflict rejection. It also checks enlarged text in blank space, custom-font DPI painting at 100/150/200/250%, unchanged scroll/focus/Escape behavior, and translated PNG output pixel-identical to the native fixture composite. Original mode and an entirely obsolete layer prevent saving. `appearance-underline.png`, `appearance-plain.png` and `appearance-contrast.png` show Arial at 150% with fitting enabled: 66 translations, zero reported clipping, and no intersection with existing English in each variant. `text-settings.png` and `toolbar.png` show the actual native/WebView controls. Physical Copilot-key interception still requires the user's real keyboard signal; the check drives the native listener's state machine without injecting keys.

`results/frontend/029_20261002T1454561387919Z_seed0/` preserves the failed large-text check: reserving only font line height omitted GDI text leading. The corrected renderer reserves the measured DrawString extent. Twelve backend regressions pass, and the C# Release build has zero warnings/errors. `results/installer/007_20261002T150314Z_seed0/` passes the compiled installer/uninstaller fixtures; full payload relocation was verified for the prior package, not repeated for this unchanged installer implementation.

`results/packaged/005_20261002T150357Z_seed0/` reaches model readiness in 102.08 seconds on the first copied-package launch. `006_20261002T150711Z_seed0/` repeats at 20.02 seconds. Both validate GPU.0 OCR and CPU translation. The repeat ten-second idle sample uses 0.047 CPU seconds across the owned tree. Its 8.58 W is the whole processor package and includes background applications, not attributable app power or battery draw. Cold translation-engine import remains slow. All checks use isolated user data and public generated content, without desktop capture, model download, dependency installation or startup registration.

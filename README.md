# Xiaomi Revamp

**Your laptop controls, local file search and screen translation, together.**

Xiaomi Revamp connects three independent Windows apps. PC Manager brings everyday laptop controls into one place. Add AI Center to find files by name, contents or meaning, and Screen Translator to read Chinese text on your screen in English. Search and translation run on your computer.

![PC Manager overview with laptop controls and hardware information](docs/screenshots/pc-manager-overview.png)

## Choose what you need

| App | What you can do | Take a tour |
| --- | --- | --- |
| **PC Manager** | Change performance, battery care and display settings; customize quick controls and shortcuts. | [PC Manager guide](source/pc-manager/README.md) |
| **AI Center / File Search** | Find a document even when you remember its subject better than its filename. | [Search guide](source/file-search/README.md) |
| **Screen Translator** | Translate Chinese text in a screen or selected region, with English displayed at the original text position. | [Translator guide](source/screen-translator/README.md) |

Each app has its own folder and executable. PC Manager coordinates shortcuts and can share its appearance with the other apps. AI Center and Screen Translator can also work independently.

## Find a file without opening a large window

Press **Ctrl twice**, type what you remember, then press **Enter**. Choose a file type or add it to your query, such as `project report pdf`. Enter searches; clicking a result opens it.

![Compact File Search with a PDF query and example document results](docs/screenshots/file-search-results.png)

Open **AI Center** when you want to choose indexed folders, manage the model or back up the index. The File Search shortcut opens only the compact bar.

## Read Chinese on your screen

Choose **Translate screen** or **Select region**, or use your configured shortcut. A floating toolbox appears while the local models prepare the translation. Switch back to the original text or press **Esc** to dismiss it. English and mixed-language labels are deliberately preserved.

![Screen Translator overview with screen, region and filter controls](docs/screenshots/screen-translator-overview.png)

The current bundled translation direction is **Chinese to English**. Translation filter refreshes the overlay as the screen changes.

## Install and start

1. Open [Downloads](https://github.com/Alex-Irae/Xiaomi-PC-Manager-Extended/releases) and choose the release you want.
2. Run **Xiaomi-Revamp-Installer.exe**. Choose the installation location, data location and startup options. PC Manager is required in the combined installer; AI Center and Screen Translator are optional.
3. Launch **PC Manager**, **AI Center**, **File Search** or **Screen Translator** from their Windows shortcuts. PC Manager's Toolbox also opens the connected apps.

Use the installer ZIP if you prefer to extract the setup files first. Keep the extracted setup and its payload together. Standalone installers, when listed on a release, install the optional apps separately.

**Source and installer versions are separate.** This branch and its screenshots describe Xiaomi Revamp **0.3.21**, with PC Manager **0.2.18**, AI Center **0.3.13** and Screen Translator **0.2.9**. At this documentation update, the latest published installer release is **v0.3.4**. Check the release tag before downloading; a source update does not replace its installer assets.

## Before you install

Use **Windows 11 x64** with Microsoft Edge WebView2 Runtime. PC Manager's firmware controls require a compatible Xiaomi laptop; the tested model is Xiaomi Book Pro 14 2026. Search and translation use Intel hardware supported by OpenVINO, with GPU, NPU or CPU execution depending on available drivers. See [requirements](REQUIREMENTS.txt) for the full compatibility list.

The packaged apps carry private runtimes and models. Initial indexing can take hours or days for a large document collection. Model loading and first-time compilation also take time; the screenshots do not demonstrate a particular loading speed.

## Shortcuts, appearance and your data

Manage connected shortcuts in **PC Manager → Keyboard**. Choose a preset or record the keys you actually press. Assigning an occupied shortcut swaps the two actions' keys. Optional apps handle their own shortcuts when PC Manager is closed.

![PC Manager Toolbox with connected apps and shared appearance](docs/screenshots/pc-manager-apps.png)

Settings let you choose themes, accents and custom colors. FileSync, when installed separately inside Xiaomi Revamp, can also follow PC Manager's appearance.

The installer selects where settings and app data live. By default, profiles are under `%LOCALAPPDATA%\XiaomiRevamp\install-<root-hash>`; models stay in their app folders. AI Center keeps a local search index and offers a built-in backup. Screen content and search documents are not sent through the shared shortcut connection.

Use Windows Settings or an app's **Uninstall.exe** to remove it. Read the uninstaller's choices before deleting saved settings or an index you want to keep.

## More pictures and development

[View the screenshot tour](docs/SCREENSHOTS.md) · [Developer reference](DEVELOPMENT.md) · [Release notes](RELEASE_NOTES.md)

Editable source lives under `source/`; installed apps use their own runtime folders. Keep a separate development checkout for changes and testing. This is an unofficial project, not a Xiaomi product. PC Manager builds on XiControl 0.16.0; its [GPLv3 license](source/pc-manager/LICENSE), upstream authorship and third-party notices are retained.

Screenshots show the current interface with demonstration data. Hardware readings, file names, progress and device states are examples, not performance measurements or your personal files.

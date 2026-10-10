# Screen Translator

**Read Chinese text in English, where it appears on your screen.**

Screen Translator recognizes Chinese locally and places an English overlay at the original text position. Choose the whole screen or a region. The translation layer lets clicks pass through so you can keep using the application underneath.

![Screen Translator overview with screen, region and translation filter controls](../../docs/screenshots/screen-translator-overview.png)

Current source: **0.2.9**, part of Xiaomi Revamp **0.3.21**. The bundled models support **Chinese to English**.

## Translate in a few steps

1. Choose **Translate screen**, **Select region**, or press your configured translation shortcut.
2. The toolbox appears in the upper-right corner while local models load and recognition runs.
3. Read the English overlay. Use **Original / translated** to compare, **Save PNG** to keep an image, or **Esc** to dismiss.

Use **Translation filter** when you want the translation refreshed as the screen changes, such as while scrolling. PC Manager's quick-panel translation action starts translation directly.

## Keep existing English intact

Only confidently recognized Chinese text is translated. English, other scripts, numbers without Chinese and mixed-language labels stay unchanged. This conservative behavior helps preserve app names and interface labels, but can skip a Chinese phrase combined with English in one recognized text box. Chinese-only Japanese characters and OCR mistakes cannot always be distinguished by script alone.

English-to-Chinese translation is not included in this model bundle.

## Make it comfortable to read

Settings provide text appearance and fitting controls, a power profile, refresh interval and translation reuse options. Amber marks text that cannot fit its available area. **Auto** follows Windows power mode; **Eco** refreshes less frequently.

![Translator settings showing model location, power profile and shared shortcuts](../../docs/screenshots/screen-translator-settings.png)

Choose a shortcut from the list or record a physical combination. When PC Manager is present, Keyboard settings there manage the shared bindings. Occupied connected shortcuts swap automatically. Following shared appearance uses PC Manager's theme and accent.

Loaded models use significant RAM. The current app indicates its model state and provides **Stop inference** and **Exit app and unload models** controls. Loading from an empty state, first-time compilation and driver changes can take longer than later uses; no fixed loading time is guaranteed.

## Installation and privacy

Choose Screen Translator in the [Xiaomi Revamp installer](../../README.md#install-and-start), or use a standalone release installer when available. Launch `Screen Translator\ScreenTranslator.exe` to open its controls; closing the controls hides them to the tray. Translation shortcuts can be used without keeping the main window open.

Use Windows 11 x64, WebView2 and Intel hardware supported by OpenVINO. CPU, GPU and NPU selection depends on supported stages and available drivers. [Requirements](../../REQUIREMENTS.txt) lists the tested platform. Packaged models and private runtimes are included; configuration, logs and compiled caches live in the installer-selected `screen-translator` profile.

OCR and translation run on your device. Images and recognized text stay in memory unless you explicitly save a screenshot. Diagnostics save local timing and device information. **Verify / restore bundled models** can download missing model files when requested; normal translation works offline once the models are available.

## For developers

`frontend/` contains the controls, `native/` owns the Windows overlay and app lifecycle, and `screen_translator/` performs recognition and translation. [Developer reference](DEVELOPMENT.md) preserves model, rendering and validation details. Use the [separate workspace workflow](../../DEVELOPMENT.md) to edit and test source.

Screenshots show the current interface with demonstration data. Hardware readings, file names, progress and device states are examples, not performance measurements or your personal files.

# PC Manager

**Everyday Xiaomi laptop controls in a familiar window and a compact quick panel.**

Change performance, look after your battery, adjust the display and keep frequently used actions close at hand. PC Manager also coordinates shortcuts and appearance for the connected apps.

![PC Manager overview](../../docs/screenshots/pc-manager-overview.png)

Current source: **0.2.18**, part of Xiaomi Revamp **0.3.21**. [Installation and requirements](../../README.md#install-and-start).

## Quick controls when you need them

Open the tray quick panel for common actions, performance modes and app links. The full window groups more detailed controls into Performance, Battery, Display, Touch and input, Keyboard, Notifications, Monitor and Settings.

![Compact quick panel with performance and connected app actions](../../docs/screenshots/pc-manager-quick-controls.png)

Choose which controls and links appear, their order, panel size and position in Settings. Open the full window from the panel when you need more detail. Hardware options depend on what your laptop supports.

## Battery care and travel

Choose a daily charge target in **Battery**. For a trip, enable travel charging to allow a full charge; the daily care limit returns after unplugging. Separate preferences for mains power and battery help match the laptop to how you use it.

![Battery page with care target and travel charging](../../docs/screenshots/pc-manager-battery.png)

## One place for shortcuts

Open **Keyboard** to choose a shortcut from the list, or choose **Press a shortcut** and press the combination yourself. Changes apply as you make them; the bottom notice confirms the result.

![Keyboard settings with recorded and connected app shortcuts](../../docs/screenshots/pc-manager-shortcuts.png)

Assigning a shortcut already used by a connected action swaps the two actions' keys. Windows-reserved or externally occupied combinations can be refused. PC Manager owns connected shortcuts while running; each optional app takes over when it is closed.

## Open your other apps

The **Toolbox** lists installed connected apps, with opening, settings and startup controls. Shared appearance lets them follow your selected theme and accent. Xiaomi's original tools appear when available on the laptop.

![Toolbox with File Search, Screen Translator and FileSync](../../docs/screenshots/pc-manager-apps.png)

## Installation, settings and limits

Install through the [Xiaomi Revamp installer](../../README.md#install-and-start), then launch PC Manager from Windows or `PC Manager\PCManager.exe`. The tray gives you quick access while the resident app runs. Hardware controls and startup integration can require administrator approval.

The tested hardware is **Xiaomi Book Pro 14 2026**, on Windows 11 x64. Other Xiaomi models may expose different controls. On a non-Xiaomi PC the interface can open, but Xiaomi firmware controls are unavailable. WebView2 is required; the packaged app includes its private runtime. See [requirements](../../REQUIREMENTS.txt).

Preferences, artwork and logs are kept in the installer-selected profile's `pc-manager` folder. Theme, accent, saved palettes and quick-panel choices are available in Settings. Use the app's uninstaller for removal and review its data-removal options.

## For developers

The UI is in `www/`; native Windows and hardware handling are in the C# sources and `Services/`. The packaged app and editable source are separate. [Developer reference](DEVELOPMENT.md) includes the retained architecture, command and validation notes. Use the [workspace development guide](../../DEVELOPMENT.md) for the current workflow.

PC Manager builds on XiControl 0.16.0; its [GPLv3 license](LICENSE), upstream authorship and third-party notices are retained.

Screenshots show the current interface with demonstration data. Hardware readings, file names, progress and device states are examples, not performance measurements or your personal files.

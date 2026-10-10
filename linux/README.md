# Xiaomi Revamp on Linux (Arch, Hyprland): first base

This folder is the start of the Linux versions of PC Manager, file search (AI Center) and Screen Translator.
FileSync lives in its own repository; its Linux and multi-device plan is in `FileSync/linux/DESIGN.md` there.

**State on 11 October 2026: written on Windows, not yet run on Linux.** No Linux system was available where this
was written. The parts that could be checked on Windows were checked (listed below). Everything that opens a
window, captures the screen or touches hardware is untested until it runs on Arch. Expect to fix things in the
first session there.

## How it is built

The Windows apps are a native host (C#, WebView2) around web pages, plus Python engines for search and translation.
On Linux:

- **The web pages are reused unchanged.** `revamp/webhost.py` shows them in GTK4 + WebKitGTK windows and gives them
  the same `window.chrome.webview` bridge they use on Windows. Panels, the search bar and the translation overlay
  are Hyprland layer surfaces (gtk4-layer-shell).
- **The Python engines are reused unchanged** (`source/file-search/xiaomi_search`, `source/screen-translator/screen_translator`),
  with small guarded edits where they assumed Windows (file attributes, path separator, index encryption, the pipe
  between the translator's processes).
- **The native hosts are rewritten in Python** (`pc-manager/pc_manager.py`, `file-search/ai_center.py`,
  `screen-translator/translator.py`). Each is one background app; starting it again passes the arguments to the
  running one, which is how Hyprland key bindings reach it.
- **PC Manager's hardware layer is new** (`pc-manager/backend.py`). Windows talks to Xiaomi's firmware through a
  Windows-only interface. Linux uses what the kernel exposes: sysfs battery and backlight files, the ACPI platform
  profile or power-profiles-daemon, `hyprctl` for the display rate and input devices.

## What exists, and how far it was checked

| Part | What it does now | Checked on Windows | Not checked |
| --- | --- | --- | --- |
| `revamp/webhost.py` | Windows, page bridge, layer surfaces, single instance | syntax only | everything at run time |
| `pc-manager/backend.py` | State, brightness, refresh rate (manual, automatic by power source), performance profiles, charge limit, travel charge, touchpad and touchscreen switches, charger wattage, telemetry | `tests/check_backend.py` against a made-up system tree: passes | real sysfs names on this laptop, permissions |
| `pc-manager/pc_manager.py` | Quick panel and manager window with the Windows pages; stay awake, prevent sleep, screen off | syntax only | windows, pages against this backend |
| `file-search/ai_center.py` | Search bar and AI Center window over the unchanged engine; open, reveal, copy, history, exclusions | the engine's own checks still pass on Windows after the portability edits | the host, the engine on Linux, the model on Linux drivers |
| `screen-translator/translator.py` | Capture a monitor or an area (grim, slurp), translate, draw the translation in place | the engine's tests still pass on Windows; the backend starts | capture, overlay, the models on Linux drivers |
| `install.sh`, `probe/probe.sh`, `hypr/revamp.conf`, `udev/` | Setup, hardware report, key bindings, access rule | not run | all of it |

Known gaps against Windows, by app:

- **PC Manager:** no settings catalogue (the long settings pages are empty), no shortcut editor (Hyprland owns the
  keys), no special-key handling (Mi key, AI key), no keyboard backlight, haptics, on-screen displays, monitor
  widget or tray icon. Whether the charge limit and performance profiles work at all depends on kernel support for
  this laptop: the probe answers that. Xiaomi's firmware calls are not ported.
- **File search:** no program results, no Windows Search channel, no "free memory when idle", no ranking by
  remembered choices, no index encryption, recent queries are a plain file.
- **Screen Translator:** no settings window, no live re-translation, no "show original" or filter shortcuts,
  models stay loaded.
- **All:** no tray icons (GTK4 has none; a Caelestia module would take that role), no installer or packaging, no
  start-with-session switch other than the `exec-once` lines.

## First session on Arch

1. Clone the repository and read `linux/install.sh`, then run it. With the Windows partition mounted, pass
   `--models-from "/mnt/windows/Program Files/Xiaomi Revamp"` to copy the models (about 700 MB).
2. Run `linux/probe/probe.sh`. Its report says which hardware controls this kernel offers. Keep it for the next step.
3. Add `source = <repository>/linux/hypr/revamp.conf` to your Hyprland configuration and reload Hyprland.
4. Start each app once from a terminal to see its errors: `revamp-pc-manager --manager`, `revamp-ai-center --center`,
   `revamp-translator --screen`.
5. Open Claude Code in the repository on Arch and continue from the probe report and those errors.

The Python packages are the Windows versions' runtime subset (`requirements.txt`). Arch ships a newer Python than
the Windows build uses (3.12); if OpenVINO has no wheel for it, the environment needs a 3.12 interpreter.

The GPU and the neural processor need Intel's Linux drivers (`intel-compute-runtime`, the NPU driver) for the
models to run on them. Without them the engines fall back to the processor, which is slower.

## Later: native Caelestia modules

The plan you chose is the web interface first, native Caelestia pieces later. `caelestia-examples/` has two small
Quickshell sketches of what "native" would mean: a quick-controls popup and a battery pill for the bar. They are
illustrations, unrun, and use plain Quickshell so they do not depend on Caelestia's internals.

# PC Manager v0.1.3

The EXE installer and extracted portable ZIP now disable the original Xiaomi popup before starting PC Manager. Uninstall offers a checkbox to restore Xiaomi's saved service and startup state; leaving it unchecked keeps the original popup disabled.

Settings now exports a portable ZIP containing preferences, profile picture, custom icons and artwork. Save it outside app data before uninstalling; Import backup validates it and restarts PC Manager with the restored settings. Keep the backup private because it may contain personal application paths. Confirmed brightness shortcuts also update the open quick-panel slider without waiting for a full device read.

The development resident passed 47 native UI regression checks with no errors and restored its preferences. The backup round trip and archive-path rejection passed, as did frontend fixtures and settings validation. The source ZIP contains no personal settings. The interactive backup import and the uninstall checkbox's Xiaomi-restore branch were not physically exercised in this final cycle; other laptop models remain unverified.

Use `PCManager-Setup.exe` for an installer, or extract `PCManager-portable.zip` and run `Install PC Manager.ps1`. Windows .NET 8 Desktop Runtime and WebView2 Runtime are required. `PCManager-source.zip` contains the corresponding source and README.

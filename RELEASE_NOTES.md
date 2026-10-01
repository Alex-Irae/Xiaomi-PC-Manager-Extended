# PC Manager v0.1.6

This is the consolidated current release after v0.1.0. It includes the English Xiaomi-style quick panel, larger manager, XiControl-backed controls and hardware monitor, plus the changes from the intermediate releases:

- Install for all users under `C:\Program Files\Xiaomi Revamp\PC Manager`, for the current user, or in a chosen folder. The app is named `PCManager.exe`, starts in the tray for the installing account, and has an entry in Windows Installed apps.
- Setup reversibly disables Xiaomi's original popup so it does not overlap Quick controls. Uninstall offers a checkbox to restore the original popup.
- Export or import a private settings ZIP containing preferences, profile picture and custom artwork. A local timestamped settings snapshot is also available. Keep backups outside app data if you plan to uninstall.
- Confirmed keyboard brightness changes update the open quick-panel slider without waiting for a full device refresh.
- Appearance supports up to 20 named color palettes. Select a saved palette to reveal Rename and Delete. Rename opens the name field only while editing; Delete retains the active colors as Custom. Undo and Redo cover these actions during the current session.

Your existing per-user settings and optional Xiaomi application paths remain under `%LOCALAPPDATA%\XiaomiAIManager` during an upgrade. Use `PCManager-Setup.exe` to install or upgrade, or extract `PCManager-portable.zip` and run `Install PC Manager.ps1`. `PCManager-source.zip` contains the corresponding source and license. The installer is unsigned.

Validation: the Release build, frontend behavior checks, native in-memory settings checks and package SHA-256 checks passed on the tested Xiaomi Book Pro 14. These checks do not establish compatibility with every Xiaomi laptop or certify a new Windows account's startup behavior. Windows x64, .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime are required. Exported settings backups may contain personal paths and images; keep them private.

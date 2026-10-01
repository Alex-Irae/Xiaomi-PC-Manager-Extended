# PC Manager v0.1.5

The application is now named `PCManager.exe` in the portable ZIP, Windows Task Manager and the installed folder. The default all-users location is `C:\Program Files\Xiaomi Revamp\PC Manager`. Setup also offers a current-user installation and a custom destination. Settings remain per-user in `%LOCALAPPDATA%\XiaomiAIManager` for compatibility with previous releases.

The installer writes a location-aware receipt, a shared Start menu shortcut for all-users installs, and a Windows uninstall entry. Its uninstaller removes the verified installation and offers the existing checkbox to restore Xiaomi's original popup service. Installing for all users registers background startup for the account running setup; other accounts can enable startup from their own Settings page.

On the tested Xiaomi Book Pro 14, the renamed build passed settings and backup checks, an isolated portable install, setup payload extraction, and the installed quick-panel command relay. The installed task targeted `PCManager.exe`, one resident was running, and restored settings matched the private pre-migration snapshot. Another laptop and a real reboot/sign-in are not certified by those checks.

Use `PCManager-Setup.exe` for installation, or extract `PCManager-portable.zip` and run `Install PC Manager.ps1`. The x64 .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime are required. `PCManager-source.zip` contains the corresponding source and license. Keep personal settings backups private.

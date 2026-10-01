# PC Manager v0.1.6

Saved color palettes can now be renamed or deleted from **Settings → Appearance**. Select a saved palette to reveal its name field and the Rename and Delete buttons. Renaming keeps the palette's colors and identity. Deleting the active palette keeps its three colors visible as Custom. The manager's Undo and Redo controls cover these changes during the current session.

The update preserves per-user settings, profile artwork, app links, and Xiaomi service isolation. The installer and portable ZIP still support the default all-users location, `C:\Program Files\Xiaomi Revamp\PC Manager`, as well as current-user and custom locations. Use `PCManager-Setup.exe` to install or upgrade; the ZIP includes `Install PC Manager.ps1`. Uninstall remains available from Windows Installed apps.

Validation: the Release build completed with zero warnings, frontend palette save/rename/delete fixture checks passed, native in-memory palette boundary checks passed, and the packaged files matched their SHA-256 manifest. These checks do not establish compatibility with every Xiaomi laptop.

The x64 .NET 8 Desktop Runtime and Microsoft Edge WebView2 Runtime are required. `PCManager-source.zip` contains the corresponding source and license. Keep exported settings backups private because they may contain personal paths and images.

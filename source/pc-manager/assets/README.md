# Editable application assets

`native-icon.svg` starts from XiControl's `tray-settings.svg` and has a thicker outline. `app.ico` renders it larger on a black background for the native tray, taskbar and Task Manager. `app.svg` remains the separate in-app fallback artwork; profile pictures chosen in Settings replace panel pictures only. Windows may cache the executable icon until the app is rebuilt and restarted.

Edit `native-icon.svg` and regenerate `app.ico` with PowerShell 7, or replace `app.ico` directly. The resident must restart for native icon changes; rebuilding and deploying is required for the executable icon. The current generator is:

```powershell
pwsh -NoProfile -File tools/render-app-icon.ps1
```

`www/osd/battery/battery*_charging.svg` and `battery*_normal.svg` preserve local Xiaomi battery shapes for AC-connect and disconnect notifications. `pwsh -NoProfile -File tools/render-charging-osd.ps1` creates `Charging*_Dark.png` and `OnBattery*_Dark.png` cards. A near-simultaneous confirmed automatic refresh-rate card appears beside the power card in the same OSD window. Changing OSD PNGs requires a resident restart.

Settings > Choose profile picture accepts PNG, JPG or BMP up to 10 MiB and 8192 pixels per side, then center-crops and resizes to 256 pixels in the per-user data `icons/` folder. Reset profile clears the selection without deleting copied pictures. No source picture is sent over the network.

For an installed copy, choose **Settings > Import asset pack**. Put any combination of the following in a folder, then select that folder. These overrides are copied to `%LOCALAPPDATA%\XiaomiAIManager\assets`, so app updates leave them in place:

```text
my-asset-pack/
  app.png                  optional logo when no profile picture is selected
  app.ico                  optional tray and native-window icon
  custom.css               optional CSS for panel/card/form layout and colors
  icons/
    battery.png            built-in icon names, 24 to 128 px recommended
    refresh.png
    settings.png
  osd/
    CapsLock_Dark.png      Xiaomi notification art, 160 x 160 recommended
    DisFre60_Dark.png
    WorkloadSpeed_En_Dark.png
```

All images must be PNG, except `app.ico`. PNGs may be at most 2048 pixels per side and 5 MB; CSS is limited to 128 KB. The importer accepts OSD names matching the bundled files and built-in icon names made of lowercase letters, digits and hyphens. It ignores unrelated files and never imports scripts or HTML. A missing or damaged override falls back to the bundled artwork. Imported web icons, logo and CSS update after the panel refreshes. Restart the resident for OSD and native icon changes; the executable's embedded icon still requires a rebuild. Removing an override file restores its bundled counterpart after a refresh or restart. The CSS file is an advanced visual override, not a device-control extension.

Quick-panel app PNG icons are also chosen in Settings. Built-in button shapes are in `www/bridge.js`; a matching `icons/<name>.png` from an asset pack takes precedence. The tools list is `www/quick-tools.js`. The icon disclosure retains its open state during background refresh. Icon selections and Restore button icons apply immediately.

Original Xiaomi dark OSD artwork is copied into `www/osd/`, with white glyphs on dark backgrounds. Matching local dark SVGs fill missing travel, unlock and touchscreen-on cases. The original light copies remain available. These are independent of the profile and native app icon. OSD style is selectable in Settings.

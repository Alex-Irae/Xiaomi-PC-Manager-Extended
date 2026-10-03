@echo off
rem Purpose: open local search directly, without Xiaomi's shortcut.
rem Dependencies: built native host, existing .venv and WebView2. Outputs: local UI/log.
rem Command: double-click this file; launch again to show an existing instance.
cd /d "%~dp0"
if not exist "native\bin\Release\net8.0-windows\AI Center.exe" (
  echo Native host missing. Build with pwsh -NoProfile -File native\build.ps1
  pause
  exit /b 1
)
start "" "native\bin\Release\net8.0-windows\AI Center.exe" --search

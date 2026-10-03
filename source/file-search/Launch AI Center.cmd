@echo off
rem Purpose: open the separate native AI Center. Dependencies: built host + backend.
rem Outputs: AI Center window, data/native.log. Command: double-click this file.
cd /d "%~dp0"
if not exist "native\bin\Release\net8.0-windows\AI Center.exe" (
  echo Native host missing. Build with pwsh -NoProfile -File native\build.ps1
  pause
  exit /b 1
)
start "" "native\bin\Release\net8.0-windows\AI Center.exe" --center

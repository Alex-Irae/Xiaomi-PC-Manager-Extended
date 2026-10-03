@echo off
rem Purpose: open the secondary test app. Dependencies: prepared test build, WebView2. Command: double-click.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\test.ps1" -View manager
if errorlevel 1 pause

@echo off
rem Purpose: open the compiled popup test app. Dependencies: PowerShell, prepared test build, WebView2.
rem Output: separate test-data settings and numbered Xiaomi OSDs. Command: double-click this file.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\test.ps1" -View popup
if errorlevel 1 pause

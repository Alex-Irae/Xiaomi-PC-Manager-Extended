@echo off
rem Purpose: prepare SDK and rebuild locally. Outputs: test build/logs. Command: double-click this file.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\test.ps1" -Prepare -Build
pause

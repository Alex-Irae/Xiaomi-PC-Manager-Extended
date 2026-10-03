@echo off
rem Purpose: open XiControl's compact strip test monitor. Dependencies: prepared build. Command: double-click.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\test.ps1" -View medium
if errorlevel 1 pause

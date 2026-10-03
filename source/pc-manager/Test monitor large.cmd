@echo off
rem Purpose: open XiControl's full graph test monitor. Dependencies: prepared build. Command: double-click.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\test.ps1" -View large
if errorlevel 1 pause

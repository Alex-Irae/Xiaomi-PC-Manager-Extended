@echo off
rem Purpose: open XiControl's single-metric test monitor. Dependencies: prepared build. Command: double-click.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\test.ps1" -View small
if errorlevel 1 pause

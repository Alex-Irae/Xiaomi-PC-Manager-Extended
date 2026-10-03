@echo off
rem Purpose: return to independent daily controls and stop the OEM UI watchdog.
rem Dependencies: Windows PowerShell, administrator approval. Output: reversible service backup.
rem Command: double-click this file; closes the original Xiaomi UI in this session.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\oem-service.ps1" -Action Disable
pause

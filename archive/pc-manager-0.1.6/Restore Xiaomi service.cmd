@echo off
rem Purpose: restore MiDeviceService's saved startup mode and running state.
rem Dependencies: Windows PowerShell, administrator approval. Output: saved action record.
rem Command: double-click this file. Other OEM startup entries remain disabled.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\oem-service.ps1" -Action Restore
pause

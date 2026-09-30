@echo off
rem Purpose: run the packaged, per-user PC Manager installer from an IExpress EXE.
rem Dependencies: Windows PowerShell and PC-Manager-portable.zip in this extracted directory.
rem Outputs: a per-user installation and Windows startup task after administrator approval.
rem Command: setup.cmd (normally launched by PC-Manager-Setup.exe).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
exit /b %errorlevel%

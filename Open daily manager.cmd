@echo off
rem Purpose: activate the installed PC Manager popup, or the local build if no installation exists.
rem Dependencies: Windows and installed or locally built PC Manager.
rem Output: quick controls window. Command: double-click this file.
if exist "%ProgramFiles%\Xiaomi Revamp\PC Manager\PCManager.exe" (
    start "" "%ProgramFiles%\Xiaomi Revamp\PC Manager\PCManager.exe" --toggle
) else (
    start "" "%~dp0app\PCManager.exe" --toggle
)

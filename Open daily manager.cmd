@echo off
rem Purpose: activate the permanent resident popup. Dependencies: Windows and built app.
rem Output: quick controls window. Command: double-click this file.
start "" "%~dp0app\XiaomiAIManager.exe" --toggle

@echo off
rem Purpose: install the existing daily build and preserve competing startup entries.
rem Dependencies: Windows PowerShell; administrator approval. Output: deployment records.
rem Command: double-click this file after building app/XiaomiAIManager.exe.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\daily.ps1" -Install

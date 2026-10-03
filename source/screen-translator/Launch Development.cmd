@echo off
rem Purpose: development launch using this workstation's existing SDK and Python.
rem Dependencies: local .NET 8/WebView2 SDK, installed Python root requirements.
rem Outputs: development DLL, data logs/settings. No EXE or installer is produced.
rem Command: "Launch Development.cmd" from this source folder, or double-click it.
pwsh -NoProfile -File "%~dp0dev.ps1" -Sdk "C:\Utils\XiaomiPc Manager\development\XiaomiAIManager\.test-environment\dotnet\dotnet.exe" -WebViewReferenceDir "C:\Utils\XiaomiPc Manager\development\XiaomiAIManager\app" -Python "C:\Program Files\Python312\python.exe"
if errorlevel 1 pause

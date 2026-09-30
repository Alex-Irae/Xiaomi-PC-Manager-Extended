# Purpose: sample the visible manager's process tree, then return it to the hidden tray state.
# Dependencies: Windows PowerShell, installed daily resident and measure-resident.ps1.
# Outputs: a fresh numbered resident measurement folder under results/.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-manager-resources.ps1 -Seconds 60
param([ValidateRange(10,600)][int]$Seconds = 60)
$ErrorActionPreference = 'Stop'
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Seconds',$Seconds)
    exit $child.ExitCode
}
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'app/XiaomiAIManager.exe'
$resident = @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
if ($resident.Count -ne 1) { throw 'Expected exactly one installed daily resident.' }
try {
    Start-Process -FilePath $exe -ArgumentList '--manager' -WindowStyle Hidden -Wait
    Start-Sleep -Seconds 3
    & (Join-Path $PSScriptRoot 'measure-resident.ps1') -ResidentId $resident[0].ProcessId -Seconds $Seconds -Label manager-visible
} finally {
    Start-Process -FilePath $exe -ArgumentList '--hide' -WindowStyle Hidden -Wait
}

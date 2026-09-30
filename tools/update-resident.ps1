# Purpose: cleanly update the existing daily resident from the checked staging build.
# Dependencies: Windows PowerShell, existing .NET/Desktop runtime, administrator approval and registered task.
# Outputs: fresh deployment transcript and result.json under .test-environment/deployments; no file deletion.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/update-resident.ps1
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$isAdmin) {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'))
    if ($child.ExitCode -ne 0) { throw ('Elevated update failed with exit code ' + $child.ExitCode) }
    return
}
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ResidentImagePath {
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static string Read(int pid) {
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try { var path = new StringBuilder(1024); int length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null; }
        finally { CloseHandle(handle); }
    }
}
'@
function Get-ManagerProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" | ForEach-Object {
        [pscustomobject]@{ ProcessId = $_.ProcessId;
            ExecutablePath = if ($_.ExecutablePath) { $_.ExecutablePath } else { [ResidentImagePath]::Read([int]$_.ProcessId) } }
    })
}
$runRoot = Join-Path $projectRoot ('.test-environment\deployments\' + (Get-Date -Format yyyyMMddTHHmmssfff))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Start-Transcript -Path (Join-Path $runRoot 'deployment.txt') | Out-Null
try {
    $dailyRoot = Join-Path $projectRoot 'app'
    $dailyExe = Join-Path $dailyRoot 'XiaomiAIManager.exe'
    $stageRoot = Join-Path $projectRoot '.test-environment\daily-next'
    if (!(Test-Path -LiteralPath (Join-Path $stageRoot 'XiaomiAIManager.exe'))) { throw 'Checked staging build is missing.' }
    $processes = @(Get-ManagerProcesses)
    if (@($processes | Where-Object { !$_.ExecutablePath }).Count -gt 0) { throw 'Cannot verify the running resident image. Update stopped.' }
    $residents = @($processes | Where-Object { $_.ExecutablePath -eq $dailyExe })
    if ($residents.Count -gt 1) { throw 'More than one daily resident exists.' }
    $taskName = 'XiaomiAIManager_' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    # Suspend recurrence during a clean update so recovery cannot race the file copy.
    $taskEnabled = (Get-ScheduledTask -TaskName $taskName).State -ne 'Disabled'
    Disable-ScheduledTask -TaskName $taskName | Out-Null
    if ($residents.Count -eq 1) {
        Start-Process -FilePath $dailyExe -ArgumentList '--quit' -WindowStyle Hidden -Wait
        for ($attempt=0; $attempt -lt 40; $attempt++) {
            if (!(Get-Process -Id $residents[0].ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Id $residents[0].ProcessId -ErrorAction SilentlyContinue) { throw 'Resident did not exit cleanly. No forced termination or update was performed.' }
    }
    Get-ChildItem -LiteralPath $stageRoot | Copy-Item -Destination $dailyRoot -Recurse -Force
    Enable-ScheduledTask -TaskName $taskName | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 8
    $active = @(Get-ManagerProcesses | Where-Object { $_.ExecutablePath -eq $dailyExe })
    if ($active.Count -ne 1) { throw 'Expected exactly one updated daily resident.' }
    [pscustomobject]@{time=(Get-Date).ToString('o');pid=$active[0].ProcessId;path=$dailyExe;assemblySha256=(Get-FileHash -LiteralPath (Join-Path $dailyRoot 'XiaomiAIManager.dll')).Hash;task=$taskName} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding UTF8
    Write-Host 'PASS clean deployment and single resident startup.'
} catch { $_ | Out-String | Set-Content -LiteralPath (Join-Path $runRoot 'error.txt'); throw }
finally {
    if ($taskEnabled -and $taskName) { Enable-ScheduledTask -TaskName $taskName | Out-Null }
    Stop-Transcript | Out-Null
}

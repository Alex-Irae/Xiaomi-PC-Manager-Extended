# Purpose: observe resident CPU, memory and whole-machine power with the panel hidden and visible.
# Dependencies: Windows PowerShell, installed daily resident, existing measure-resident.ps1. No packages.
# Outputs: fresh numbered protocol and sample folders under results. Restores hidden tray state.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-popup-resources.ps1 -Seconds 60
param([ValidateRange(10,600)][int]$Seconds = 60)
$ErrorActionPreference = 'Stop'
$taskAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$taskAdmin) {
    $taskChild = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Seconds',$Seconds)
    exit $taskChild.ExitCode
}
$taskProject = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskProject 'app/PCManager.exe'
$taskResident = @(Get-CimInstance Win32_Process -Filter "Name='PCManager.exe'" | Where-Object { $_.ExecutablePath -eq $taskExe })
if ($taskResident.Count -ne 1) { throw 'Expected exactly one verified daily resident.' }
$taskRoot = Join-Path $taskProject 'results'
$taskHighest = 0
Get-ChildItem -LiteralPath $taskRoot -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $taskHighest = [Math]::Max($taskHighest,[int]$Matches[1]) } }
$taskRun = Join-Path $taskRoot ('{0:D3}_{1}_popup_resources' -f ($taskHighest+1),(Get-Date -Format yyyyMMddTHHmmssfff))
New-Item -ItemType Directory -Path $taskRun | Out-Null
@{seed=$null;secondsPerCondition=$Seconds;residentId=$taskResident[0].ProcessId;assemblySha256=(Get-FileHash (Join-Path $taskProject 'app/PCManager.dll')).Hash;protocol='Hidden then visible quick panel, idle in each state. No repeated animations or hardware writes. All other workloads uncontrolled.';limitations='Whole-machine watts cannot isolate app or animation power. Summed working sets include shared pages. Caller should avoid interacting during each observation.'} | ConvertTo-Json | Set-Content (Join-Path $taskRun 'config.json') -Encoding UTF8
$taskRuns = [Collections.Generic.List[object]]::new()
$taskErrors = [Collections.Generic.List[string]]::new()
try {
    foreach ($taskCondition in @('hidden','popup-visible')) {
        Start-Process -FilePath $taskExe -ArgumentList '--hide' -WindowStyle Hidden -Wait
        if ($taskCondition -eq 'popup-visible') { Start-Process -FilePath $taskExe -ArgumentList '--toggle' -WindowStyle Hidden -Wait }
        Start-Sleep -Seconds 2
        $taskKnown = @(Get-ChildItem -LiteralPath $taskRoot -Directory | Select-Object -ExpandProperty Name)
        & (Join-Path $PSScriptRoot 'measure-resident.ps1') -ResidentId $taskResident[0].ProcessId -Seconds $Seconds -Label $taskCondition
        $taskMeasured = Get-ChildItem -LiteralPath $taskRoot -Directory | Where-Object { $_.Name.EndsWith('_resident') -and $_.Name -notin $taskKnown } | Sort-Object Name -Descending | Select-Object -First 1
        if (!$taskMeasured) { throw 'The measurement did not create its expected report.' }
        $taskRuns.Add(@{condition=$taskCondition;path=$taskMeasured.FullName;summary=(Get-Content (Join-Path $taskMeasured.FullName 'summary.json') -Raw | ConvertFrom-Json)})
    }
} catch { $taskErrors.Add($_.Exception.Message); throw }
finally {
    Start-Process -FilePath $taskExe -ArgumentList '--hide' -WindowStyle Hidden -Wait
    @{time=(Get-Date).ToString('o');runs=$taskRuns.ToArray();errors=$taskErrors.ToArray();finalState='Hidden tray resident'} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $taskRun 'summary.json') -Encoding UTF8
}


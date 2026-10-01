# Purpose: measure the large manager window and record which resident processes use memory.
# Dependencies: installed elevated resident, measure-resident.ps1 and Windows PowerShell.
# Outputs: a fresh numbered resident measurement with process-breakdown.json; restores hidden windows.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-foreground.ps1 -Seconds 30
param([ValidateRange(10,600)][int]$Seconds = 30)
$ErrorActionPreference = 'Stop'
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Seconds',$Seconds)
    exit $child.ExitCode
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $projectRoot 'app\PCManager.exe'
$resident = @(Get-CimInstance Win32_Process -Filter "Name='PCManager.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
if ($resident.Count -ne 1) { throw 'Expected exactly one verified daily resident.' }
try {
    Start-Process -FilePath $exe -ArgumentList '--manager' -WindowStyle Hidden -Wait
    Start-Sleep -Seconds 3
    & (Join-Path $PSScriptRoot 'measure-resident.ps1') -ResidentId $resident[0].ProcessId -Seconds $Seconds -Label 'final-manager'
    $result = Get-ChildItem (Join-Path $projectRoot 'results') -Directory | Where-Object { $_.Name.EndsWith('_resident') } | Sort-Object Name -Descending | Select-Object -First 1
    $inventory = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add([int]$resident[0].ProcessId)
    do { $added = $false; foreach ($item in $inventory) { if ($ids.Contains([int]$item.ParentProcessId) -and $ids.Add([int]$item.ProcessId)) { $added = $true } } } while ($added)
    $processes = @($ids | ForEach-Object {
        $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
        if ($process) { [pscustomobject]@{ name=$process.ProcessName; pid=$process.Id; workingSetMiB=[math]::Round($process.WorkingSet64/1MB,1); privateMiB=[math]::Round($process.PrivateMemorySize64/1MB,1) } }
    } | Sort-Object workingSetMiB -Descending)
    @{ time=(Get-Date).ToString('o'); processes=$processes; totalWorkingSetMiB=[math]::Round(($processes | Measure-Object workingSetMiB -Sum).Sum,1); totalPrivateMiB=[math]::Round(($processes | Measure-Object privateMiB -Sum).Sum,1) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $result.FullName 'process-breakdown.json') -Encoding UTF8
} finally {
    Start-Process -FilePath $exe -ArgumentList '--hide' -WindowStyle Hidden -Wait
}

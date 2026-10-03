# Purpose: compare committed private memory and working set for one app process tree.
# Dependencies: administrator Windows PowerShell 5.1 and a running target process; no packages.
# Outputs: a new numbered results folder with configuration, samples, process breakdown and summary.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/compare-memory.ps1 -RootId 1234 -ExpectedName XiaomiAIManager -Label ours-hidden -Seconds 60
param(
    [Parameter(Mandatory=$true)][int]$RootId,
    [Parameter(Mandatory=$true)][string]$ExpectedName,
    [Parameter(Mandatory=$true)][ValidateSet('ours-hidden','ours-manager','ours-closed','xicontrol-idle','xiaomi-visible','xiaomi-background')][string]$Label,
    [ValidateRange(15,600)][int]$Seconds = 60,
    [ValidateRange(0,120)][int]$WarmupSeconds = 15,
    [string[]]$ExtraProcessNames = @()
)
$ErrorActionPreference = 'Stop'
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $launchArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),
        '-RootId',$RootId,'-ExpectedName',$ExpectedName,'-Label',$Label,
        '-Seconds',$Seconds,'-WarmupSeconds',$WarmupSeconds)
    if ($ExtraProcessNames.Count) { $launchArgs += '-ExtraProcessNames'; $launchArgs += $ExtraProcessNames }
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $launchArgs
    exit $child.ExitCode
}
$root = Get-Process -Id $RootId -ErrorAction Stop
if ($root.ProcessName -ne $ExpectedName) { throw "PID $RootId is $($root.ProcessName), not $ExpectedName." }
$rootStart = $root.StartTime
$projectRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $projectRoot 'results'
$highest = 0
Get-ChildItem -LiteralPath $resultsRoot -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $highest = [Math]::Max($highest,[int]$Matches[1]) } }
$run = Join-Path $resultsRoot ('{0:D3}_{1}_memory_{2}' -f ($highest+1),(Get-Date -Format yyyyMMddTHHmmssfff),$Label)
New-Item -ItemType Directory -Path $run | Out-Null
[pscustomobject]@{label=$Label;rootPid=$RootId;expectedName=$ExpectedName;rootStarted=$rootStart;
    seconds=$Seconds;warmupSeconds=$WarmupSeconds;intervalSeconds=3;extraProcessNames=$ExtraProcessNames;
    seed=$null;protocol='Passive snapshots. Primary is the root and its descendants; extras are named OEM helpers outside that tree.';
    privateDefinition='PrivateMemorySize64 is private committed bytes; summed private bytes exclude most shared WebView pages.';
    workingSetDefinition='WorkingSet64 includes resident shared pages and can double-count them across processes.'} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $run 'config.json') -Encoding UTF8
$samples = [Collections.Generic.List[object]]::new()
$processRows = [Collections.Generic.List[object]]::new()
if ($WarmupSeconds) { Write-Host "Warming $Label for $WarmupSeconds seconds..."; Start-Sleep -Seconds $WarmupSeconds }
$timer = [Diagnostics.Stopwatch]::StartNew()
while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
    $rootNow = Get-Process -Id $RootId -ErrorAction Stop
    if ($rootNow.StartTime -ne $rootStart) { throw 'The root PID changed identity during sampling.' }
    $inventory = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name)
    $primary = [Collections.Generic.HashSet[int]]::new()
    [void]$primary.Add($RootId)
    do {
        $added = $false
        foreach ($item in $inventory) {
            if ($primary.Contains([int]$item.ParentProcessId) -and $primary.Add([int]$item.ProcessId)) { $added = $true }
        }
    } while ($added)
    $extras = [Collections.Generic.HashSet[int]]::new()
    foreach ($item in $inventory) {
        if ($ExtraProcessNames -contains $item.Name -and !$primary.Contains([int]$item.ProcessId)) { [void]$extras.Add([int]$item.ProcessId) }
    }
    $all = [Collections.Generic.HashSet[int]]::new($primary)
    $all.UnionWith($extras)
    $private = 0L; $working = 0L; $webview = 0L; $extraPrivate = 0L; $count = 0
    foreach ($id in $all) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (!$process) { continue }
        try {
            $bytes = [long]$process.PrivateMemorySize64
            $residentBytes = [long]$process.WorkingSet64
            $kind = if ($primary.Contains($id)) { 'primary' } else { 'extra' }
            if ($kind -eq 'primary') {
                $private += $bytes; $working += $residentBytes
                if ($process.ProcessName -like 'msedgewebview2*') { $webview += $bytes }
            } else { $extraPrivate += $bytes }
            $count++
            $processRows.Add([pscustomobject]@{seconds=[math]::Round($timer.Elapsed.TotalSeconds,2);kind=$kind;
                pid=$id;name=$process.ProcessName;privateMiB=[math]::Round($bytes/1MB,2);
                workingSetMiB=[math]::Round($residentBytes/1MB,2)})
        } catch { continue }
    }
    $samples.Add([pscustomobject]@{seconds=[math]::Round($timer.Elapsed.TotalSeconds,2);
        primaryPrivateMiB=[math]::Round($private/1MB,2);primaryWorkingSetMiB=[math]::Round($working/1MB,2);
        webViewPrivateMiB=[math]::Round($webview/1MB,2);extraPrivateMiB=[math]::Round($extraPrivate/1MB,2);
        processCount=$count})
    Write-Host ('{0} sample {1}: private {2:N1} MiB, working set {3:N1} MiB, WebView private {4:N1} MiB, extras {5:N1} MiB' -f
        $Label,$samples.Count,($private/1MB),($working/1MB),($webview/1MB),($extraPrivate/1MB))
    Start-Sleep -Seconds 3
}
$samples | Export-Csv -LiteralPath (Join-Path $run 'samples.csv') -NoTypeInformation -Encoding UTF8
$processRows | Export-Csv -LiteralPath (Join-Path $run 'processes.csv') -NoTypeInformation -Encoding UTF8
$summary = [pscustomobject]@{label=$Label;rootPid=$RootId;samples=$samples.Count;
    meanPrimaryPrivateMiB=($samples | Measure-Object primaryPrivateMiB -Average).Average;
    peakPrimaryPrivateMiB=($samples | Measure-Object primaryPrivateMiB -Maximum).Maximum;
    meanPrimaryWorkingSetMiB=($samples | Measure-Object primaryWorkingSetMiB -Average).Average;
    meanWebViewPrivateMiB=($samples | Measure-Object webViewPrivateMiB -Average).Average;
    meanExtraPrivateMiB=($samples | Measure-Object extraPrivateMiB -Average).Average;
    interpretation='One short observational run on this laptop. Private commitment and working set are different quantities; no battery-life inference.'}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding UTF8
Write-Host ('Saved ' + $run)
$summary | ConvertTo-Json

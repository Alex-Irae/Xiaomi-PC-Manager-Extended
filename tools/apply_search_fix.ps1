# Purpose: apply the verified encrypted-index reload/capacity fixes while retaining runtimes and personal data.
# Dependencies: administrator PowerShell 5.1, installed suite and refreshed release 005.
# Outputs: previous backend file backup and updated installation ownership metadata.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/apply_search_fix.ps1 -RunDirectory ABSOLUTE_PATH
param([Parameter(Mandatory=$true)][string]$RunDirectory,[string]$PatchList='')
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$runRoot=[IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
if (!$runRoot.StartsWith($sourceRoot+'\results\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay inside suite/results.' }
trap { [IO.File]::AppendAllText((Join-Path $runRoot 'search-patch-error.txt'),($_|Out-String)); exit 1 }
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required for the owned Program Files update.' }
$root='C:\Program Files\Xiaomi Revamp Suite'
$marker=Get-Content -LiteralPath (Join-Path $root 'suite-install.json') -Raw | ConvertFrom-Json
if ($marker.profileId -ne 'install-34276a167be8') { throw 'Suite identity mismatch.' }
$manifestPath=Join-Path $root 'suite-owned.json'
$owned=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$release=Get-Content -LiteralPath (Join-Path $sourceRoot 'packages\005_20261003T050452Z\packages.json') -Raw | ConvertFrom-Json
$relatives=@('xiaomi_search/protection.py','xiaomi_search/store.py','xiaomi_search/indexer.py')
$summaryName='capacity-patch-summary.json'
if ($PatchList) {
    $patchPath=[IO.Path]::GetFullPath($PatchList)
    if (!$patchPath.StartsWith($runRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Patch list must stay inside the evidence directory.' }
    $relatives=Get-Content -LiteralPath $patchPath -Raw | ConvertFrom-Json
    $summaryName='paused-worker-patch-summary.json'
}
foreach($relative in $relatives) {
    $source=Join-Path $sourceRoot ('install\AI Center\'+$relative)
    $target=Join-Path $root ('AI Center\'+$relative)
    if (![IO.Path]::GetFullPath($target).StartsWith($root+'\AI Center\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Patch path escapes the component.' }
    for($path=$target;$path;$path=Split-Path -Parent $path) { if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected patch target.' } }
    if ((Get-FileHash -LiteralPath $target).Hash -ne $owned.'file-search'.files.$relative -or (Get-FileHash -LiteralPath $source).Hash -ne $release.components.'file-search'.files.$relative) { throw 'Backend patch hashes do not match the inspected payload.' }
}
$statusPath=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'XiaomiRevampSuite\install-34276a167be8\shared\status-file-search.json'
$status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
& (Join-Path $root 'AI Center\AI Center.exe') --quit
$watch=[Diagnostics.Stopwatch]::StartNew()
while (Get-Process -Id $status.Pid -ErrorAction SilentlyContinue) { if ($watch.Elapsed.TotalSeconds -gt 15) { throw 'Search did not shut down cleanly.' }; Start-Sleep -Milliseconds 150 }
$backupFolder='capacity-patch-backup-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
foreach($relative in $relatives) {
    $source=Join-Path $sourceRoot ('install\AI Center\'+$relative)
    $target=Join-Path $root ('AI Center\'+$relative)
    $backup=Join-Path $runRoot ($backupFolder+'\'+$relative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    Copy-Item -LiteralPath $target -Destination $backup
    Copy-Item -LiteralPath $source -Destination $target -Force
}
foreach($component in @('pc-manager','file-search','screen-translator')) { $owned.$component=$release.components.$component }
[IO.File]::WriteAllText($manifestPath,($owned|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $runRoot $summaryName),(@{passed=$true;changedBackendFiles=$relatives;userIndexRetained=$true;backup=$backupFolder;release='005_20261003T050452Z'}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Output 'PASS: installed reload/capacity fixes applied; encrypted index and user settings retained.'

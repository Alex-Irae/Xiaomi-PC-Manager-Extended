# Purpose: update verified suite binaries and observe real shortcut ownership handover.
# Dependencies: administrator PowerShell 5.1, the installed suite and an inspected patch-plan.json.
# Outputs: binary backups, lifecycle evidence and the updated installation ownership manifest.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/apply_live_patch.ps1 -RunDirectory ABSOLUTE_PATH
param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$runRoot = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
if (!$runRoot.StartsWith($sourceRoot + '\results\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay inside suite/results.' }
trap {
    [IO.File]::AppendAllText((Join-Path $runRoot 'patch-preflight-errors.txt'), $_.Exception.ToString() + [Environment]::NewLine + $_.InvocationInfo.PositionMessage + [Environment]::NewLine)
    exit 1
}
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required to replace the verified Program Files binaries.' }
$root = 'C:\Program Files\Xiaomi Revamp Suite'
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'XiaomiRevampSuite\install-34276a167be8'
$shared = Join-Path $data 'shared'
$marker = Get-Content -LiteralPath (Join-Path $root 'suite-install.json') -Raw | ConvertFrom-Json
if ($marker.profileId -ne 'install-34276a167be8' -or $marker.portable) { throw 'Suite identity mismatch.' }
$ownedPath = Join-Path $root 'suite-owned.json'
$owned = Get-Content -LiteralPath $ownedPath -Raw | ConvertFrom-Json
$plan = Get-Content -LiteralPath (Join-Path $runRoot 'patch-plan.json') -Raw | ConvertFrom-Json
foreach ($file in $plan) {
    $destination = [IO.Path]::GetFullPath($file.destination)
    $source = [IO.Path]::GetFullPath($file.source)
    if (!$destination.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or !$source.StartsWith($sourceRoot + '\install\', [StringComparison]::OrdinalIgnoreCase)) { throw 'A patch path is outside its verified scope.' }
    for ($parent = $destination; $parent; $parent = Split-Path -Parent $parent) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Redirected patch path: ' + $parent) }
    }
    if ($file.previousHash) {
        if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.previousHash) { throw ('An installed file changed after inspection: ' + $destination) }
    } elseif (Test-Path -LiteralPath $destination) { throw ('Unexpected existing file: ' + $destination) }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.newHash) { throw ('A patch source changed after inspection: ' + $source) }
}
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = 'XiaomiRevampSuite_34276A167BE8_' + $sid
$task = Get-ScheduledTask -TaskName $taskName
if (@($task.Actions).Count -ne 1 -or !$task.Actions[0].Execute.StartsWith($root + '\PC Manager\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Startup task is not owned by this installed suite.' }
$enabled = $task.State -ne 'Disabled'
function Status([string]$component) { Get-Content -LiteralPath (Join-Path $shared ('status-' + $component + '.json')) -Raw | ConvertFrom-Json }
function WaitFor([scriptblock]$condition, [string]$message) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 15) { if (& $condition) { Write-Output ('PASS: ' + $message); return }; Start-Sleep -Milliseconds 150 }
    throw ('Timed out: ' + $message)
}
$summary = [ordered]@{ passed=$false; realAppHandover=$false; hubRestart=$false; files=$plan.Count }
Start-Transcript -LiteralPath (Join-Path $runRoot 'live-patch.log') | Out-Null
try {
    Disable-ScheduledTask -TaskName $taskName | Out-Null
    $hubBefore = Status 'pc-manager'; $searchBefore = Status 'file-search'; $translatorBefore = Status 'screen-translator'
    & (Join-Path $root 'PC Manager\PCManager.exe') --quit
    WaitFor { !(Get-Process -Id $hubBefore.Pid -ErrorAction SilentlyContinue) } 'installed PC Manager exited cleanly'
    WaitFor { $a=Status 'file-search'; $b=Status 'screen-translator'; $a.Owner -eq 'Standalone' -and $b.Owner -eq 'Standalone' -and $a.Owned -contains 'file-search' -and $b.Owned -contains 'screen-translator' -and !$a.Error -and !$b.Error } 'both real optional apps acquired their own shortcuts'
    $summary.realAppHandover = $true
    $summary.fallback = @{ search=(Status 'file-search'); translator=(Status 'screen-translator') }
    & (Join-Path $root 'AI Center\AI Center.exe') --quit
    # Use the app's existing named shutdown protocol, documented in native/DesktopOptions.cs.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SuiteShutdown {
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr a, IntPtr b);
}
'@
    $quit = [SuiteShutdown]::RegisterWindowMessage('LocalScreenTranslator.Quit.Suite.34276A167BE8')
    [SuiteShutdown]::PostMessage([IntPtr]65535, $quit, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    WaitFor { !(Get-Process -Id $searchBefore.Pid -ErrorAction SilentlyContinue) -and !(Get-Process -Id $translatorBefore.Pid -ErrorAction SilentlyContinue) } 'optional apps exited through their own shutdown commands'
    $backupRoot = Join-Path $runRoot 'binary-backup'
    foreach ($file in $plan) {
        $relative = $file.destination.Substring($root.Length + 1)
        $backup = Join-Path $backupRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
        if (Test-Path -LiteralPath $file.destination) { Copy-Item -LiteralPath $file.destination -Destination $backup }
        Copy-Item -LiteralPath $file.source -Destination $file.destination -Force
        if ((Get-FileHash -LiteralPath $file.destination -Algorithm SHA256).Hash -ne $file.newHash) { throw ('Patch verification failed: ' + $relative) }
        $owned.($file.component).files | Add-Member -NotePropertyName $file.relative -NotePropertyValue $file.newHash.ToLowerInvariant() -Force
        Write-Output ('PASS: updated ' + $relative)
    }
    [IO.File]::WriteAllText($ownedPath, ($owned | ConvertTo-Json -Depth 100), (New-Object Text.UTF8Encoding($false)))
    Start-Process -FilePath (Join-Path $root 'PC Manager\PCManager.exe') -ArgumentList '--manager' -WindowStyle Hidden | Out-Null
    WaitFor { $a=Status 'pc-manager'; $a.Pid -ne $hubBefore.Pid -and (Get-Process -Id $a.Pid -ErrorAction SilentlyContinue) -and !$a.Error -and $a.Owned -contains 'file-search' -and $a.Owned -contains 'screen-translator' } 'updated PC Manager restarted and reclaimed both shortcut groups'
    $summary.hubRestart = $true; $summary.hub = Status 'pc-manager'; $summary.passed = $true
} catch { $summary.error=$_.Exception.ToString(); throw }
finally {
    if ($enabled) { Enable-ScheduledTask -TaskName $taskName | Out-Null }
    [IO.File]::WriteAllText((Join-Path $runRoot 'live-patch-summary.json'), ($summary | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Stop-Transcript | Out-Null
}

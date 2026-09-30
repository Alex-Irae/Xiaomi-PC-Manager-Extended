# Purpose: remove this user's PC Manager installation and app-owned data, restoring OEM service isolation first.
# Dependencies: Windows PowerShell, installed tools/oem-service.ps1, administrator approval.
# Outputs: removes the app, startup, API firewall rule, shortcuts, registry entry and app-owned data.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File "Uninstall PC Manager.ps1"
param([string]$ExpectedSid = '')
$ErrorActionPreference = 'Stop'

$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedSid -and $ExpectedSid -ne $currentSid) {
    throw 'The administrator prompt selected a different Windows account. Uninstall while signed in to an administrator account.'
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$isAdmin) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-ExpectedSid', $currentSid)
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $args
    exit $child.ExitCode
}

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PC Manager'
$expectedRoot = [IO.Path]::GetFullPath($installRoot).TrimEnd('\')
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if (!$scriptRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This uninstaller is not running from the expected per-user PC Manager directory.'
}
$dataRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'XiaomiAIManager'))
$apiRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'XiaomiAIManager'))
foreach ($root in @($installRoot, $dataRoot, $apiRoot)) {
    if ((Test-Path -LiteralPath $root) -and ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a redirected application directory: $root"
    }
}
$exe = Join-Path $installRoot 'XiaomiAIManager.exe'
$taskName = 'XiaomiAIManager_' + $currentSid
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$taskWasEnabled = $task -and $task.State -ne 'Disabled'
$completed = $false
if ($task) {
    if (@($task.Actions | Where-Object { $_.Execute -eq $exe }).Count -ne 1) {
        throw 'The startup task does not point to this installation. No task or files were removed.'
    }
    Disable-ScheduledTask -TaskName $taskName | Out-Null
}
try {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" |
        Where-Object { $_.ExecutablePath -eq $exe })
    if ($running.Count) {
        Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -Wait
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $remaining = @($running | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
            if (!$remaining.Count) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($remaining.Count) {
            throw 'PC Manager did not exit cleanly; no installed files were removed.'
        }
    }

    $intent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) `
        ('XiaomiAIManager\service-state\' + $currentSid + '\oem-service-isolation.json')
    if (Test-Path -LiteralPath $intent) {
        $isolation = Get-Content -LiteralPath $intent -Raw | ConvertFrom-Json
        if ($isolation.enabled) {
            $restore = Join-Path $installRoot 'tools\oem-service.ps1'
            if (!(Test-Path -LiteralPath $restore)) { throw 'OEM isolation is active, but its restore script is missing. Uninstall stopped.' }
            & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $restore -Action Restore
            if ($LASTEXITCODE -ne 0) { throw 'OEM service restoration failed. The app and recovery records were preserved.' }
        }
    }

    $firewall = @(Get-NetFirewallRule -DisplayName 'XiaomiAIManager API' -ErrorAction SilentlyContinue)
    if ($firewall.Count) { $firewall | Remove-NetFirewallRule }
    if ($task) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    $legacy = Get-ScheduledTask -TaskName 'XiaomiAIManager' -ErrorAction SilentlyContinue
    if ($legacy -and @($legacy.Actions | Where-Object { $_.Execute -eq $exe }).Count -eq 1) {
        Unregister-ScheduledTask -TaskName 'XiaomiAIManager' -Confirm:$false
    }
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'PC Manager.lnk'
    if (Test-Path -LiteralPath $startMenu) {
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
        if ($shortcut.TargetPath -eq $exe) { Remove-Item -LiteralPath $startMenu }
    }
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiAIManager'
    if ((Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).InstallLocation -eq $installRoot) {
        Remove-Item -LiteralPath $uninstallKey
    }
    # Only fixed, validated app-owned directories are removed. Shared runtimes are outside these roots.
    if (Test-Path -LiteralPath $apiRoot) { Remove-Item -LiteralPath $apiRoot -Recurse -Force }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }
    Remove-Item -LiteralPath $installRoot -Recurse -Force
    $completed = $true
    Write-Host 'PASS PC Manager app files, settings, cache, startup, shortcut, API data and firewall rule removed.'
} finally {
    if (!$completed -and $taskWasEnabled) { Enable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null }
}

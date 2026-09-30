# Purpose: remove this user's PC Manager app and startup task, restoring OEM service isolation first.
# Dependencies: Windows PowerShell, installed tools/oem-service.ps1, administrator approval.
# Outputs: removes this app's files/shortcuts/task; preserves LocalAppData\XiaomiAIManager settings and backups.
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
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -Wait -PassThru -ArgumentList $args
    exit $child.ExitCode
}

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PC Manager'
$expectedRoot = [IO.Path]::GetFullPath($installRoot).TrimEnd('\')
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if (!$scriptRoot.Equals($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This uninstaller is not running from the expected per-user PC Manager directory.'
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
        Start-Process -FilePath $exe -ArgumentList '--quit' -Wait
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if (!(Get-Process -Id $running[0].ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Id $running[0].ProcessId -ErrorAction SilentlyContinue) {
            throw 'PC Manager did not exit cleanly; no installed files were removed.'
        }
    }

    $intent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData'))
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

    if ($task) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'PC Manager.lnk'
    if (Test-Path -LiteralPath $startMenu) {
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
        if ($shortcut.TargetPath -eq $exe) { Remove-Item -LiteralPath $startMenu }
    }
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiAIManager'
    if ((Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).InstallLocation -eq $installRoot) {
        Remove-Item -LiteralPath $uninstallKey
    }
    # The target was resolved and checked above; user data stays outside this directory.
    Remove-Item -LiteralPath $installRoot -Recurse -Force
    $completed = $true
    Write-Host 'PASS PC Manager removed. Your settings and OEM recovery records remain in LocalAppData\XiaomiAIManager.'
} finally {
    if (!$completed -and $taskWasEnabled) { Enable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null }
}

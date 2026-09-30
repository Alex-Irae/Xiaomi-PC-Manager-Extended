# Purpose: install the packaged PC Manager for the current Windows user.
# Dependencies: Windows PowerShell, PC-Manager-portable.zip, .NET 8 Desktop Runtime, WebView2 Runtime.
# Outputs: %LOCALAPPDATA%\Programs\PC Manager, Start menu shortcut, per-user startup task.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
param([string]$ExpectedSid = '')
$ErrorActionPreference = 'Stop'

$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedSid -and $ExpectedSid -ne $currentSid) {
    throw 'The administrator prompt selected a different Windows account. Run setup while signed in to an administrator account.'
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$isAdmin) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-ExpectedSid', $currentSid)
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -Wait -PassThru -ArgumentList $args
    exit $child.ExitCode
}

$package = Join-Path $PSScriptRoot 'PC-Manager-portable.zip'
if (!(Test-Path -LiteralPath $package)) { throw 'The application payload is missing from setup.' }
foreach ($runtime in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
    $runtimeRoot = Join-Path $env:ProgramFiles ('dotnet\shared\' + $runtime)
    if (!(Test-Path -LiteralPath $runtimeRoot) -or
        !@(Get-ChildItem -LiteralPath $runtimeRoot -Directory | Where-Object { $_.Name -match '^8\.' }).Count) {
        throw 'Install the Microsoft .NET 8 Desktop Runtime (x64) before PC Manager: https://dotnet.microsoft.com/download/dotnet/8.0'
    }
}
$webViewClient = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$webViewKeys = @(
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + $webViewClient,
    'HKCU:\Software\Microsoft\EdgeUpdate\Clients\' + $webViewClient
)
$hasWebView = @($webViewKeys | Where-Object {
    $version = (Get-ItemProperty -LiteralPath $_ -Name pv -ErrorAction SilentlyContinue).pv
    $version -and $version -ne '0.0.0.0'
}).Count -gt 0
if (!$hasWebView) {
    throw 'Install the Microsoft Edge WebView2 Evergreen Runtime before PC Manager: https://developer.microsoft.com/microsoft-edge/webview2/'
}
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PC Manager'
$exe = Join-Path $installRoot 'XiaomiAIManager.exe'
$taskName = 'XiaomiAIManager_' + $currentSid
$oldTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$oldTaskWasEnabled = $oldTask -and $oldTask.State -ne 'Disabled'
$taskReplaced = $false
if ($oldTask) {
    $actions = @($oldTask.Actions | Where-Object { $_.Execute })
    if ($actions.Count -ne 1 -or [IO.Path]::GetFileName($actions[0].Execute.Trim('"')) -ne 'XiaomiAIManager.exe') {
        throw 'The existing startup task has an unexpected action. No files were changed.'
    }
    Disable-ScheduledTask -TaskName $taskName | Out-Null
}
try {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'")
    if (@($running | Where-Object { !$_.ExecutablePath }).Count -gt 0) {
        throw 'A PC Manager process could not be identified. Close it before installing.'
    }
    $knownPaths = @($exe)
    if ($oldTask) { $knownPaths += $actions[0].Execute.Trim('"') }
    $unknown = @($running | Where-Object { $knownPaths -notcontains $_.ExecutablePath })
    if ($unknown.Count) { throw 'Another PC Manager or test instance is running. Exit it from its tray icon before installing.' }
    if ($running.Count) {
        $oldExe = $running[0].ExecutablePath
        Start-Process -FilePath $oldExe -ArgumentList '--quit' -Wait
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if (!(Get-Process -Id $running[0].ProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Id $running[0].ProcessId -ErrorAction SilentlyContinue) {
            throw 'PC Manager did not exit cleanly. Installation stopped without forcing it closed.'
        }
    }

    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Expand-Archive -LiteralPath $package -DestinationPath $installRoot -Force
    if (!(Test-Path -LiteralPath $exe)) { throw 'The packaged executable was not extracted.' }

    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'PC Manager.lnk'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = '--manager'
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.IconLocation = $exe + ',0'
    $shortcut.Save()

    $uninstall = Join-Path $installRoot 'Uninstall PC Manager.ps1'
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiAIManager'
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'PC Manager' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '0.1.0' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'PC Manager project' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstall + '"') -PropertyType String -Force | Out-Null

    # Fresh installations leave Xiaomi's OEM service and startup entries alone.
    Start-Process -FilePath $exe -ArgumentList '--install-startup' -WorkingDirectory $installRoot | Out-Null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($task -and @($task.Actions | Where-Object { $_.Execute -eq $exe }).Count -eq 1) { break }
    }
    if (!$task -or @($task.Actions | Where-Object { $_.Execute -eq $exe }).Count -ne 1) {
        throw 'The files were installed, but Windows did not confirm PC Manager startup registration.'
    }
    $taskReplaced = $true
    Write-Host ('PASS PC Manager installed at ' + $installRoot)
    Write-Host 'The Start menu shortcut opens the main manager. Settings and logs remain under LocalAppData\XiaomiAIManager.'
} finally {
    if (!$taskReplaced -and $oldTaskWasEnabled) { Enable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null }
}

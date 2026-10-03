# Purpose: install PC Manager in a chosen per-user or shared location.
# Dependencies: Windows PowerShell 5.1, .NET 8 Desktop Runtime, WebView2 Runtime, payload.zip beside this file.
# Outputs: application files, per-user startup task, shortcut, and scoped uninstall registration.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File Install.ps1 [-Scope AllUsers|CurrentUser] [-Destination C:\path] [-NoStartup]
param([ValidateSet('AllUsers','CurrentUser')][string]$Scope = 'AllUsers', [string]$Destination = '',
    [switch]$NoStartup, [switch]$Quiet, [string]$ExpectedSid = '')
$ErrorActionPreference = 'Stop'
$previousTaskXml = $null
$taskName = $null
$startupConfirmed = $false
$oemWasIsolated = $true
$oemScript = $null

function Show-Result([string]$message, [bool]$failed = $false) {
    Set-Content -LiteralPath (Join-Path $PSScriptRoot 'install-result.txt') -Value $message -Encoding UTF8
    Write-Host $message
}

try {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($ExpectedSid -and $sid -ne $ExpectedSid) {
        throw 'The administrator prompt selected a different Windows account. Install while signed into an administrator account.'
    }
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (!$Destination) {
        $Destination = if ($Scope -eq 'AllUsers') { Join-Path $env:ProgramFiles 'Xiaomi Revamp\PC Manager' }
            else { Join-Path $env:LOCALAPPDATA 'Programs\Xiaomi Revamp\PC Manager' }
    }
    if ($Destination.Contains('"')) { throw 'Installation path cannot contain quotation marks.' }
    if (!$NoStartup -and !$admin) {
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),
            '-ExpectedSid',$sid,'-Scope',$Scope,'-Destination',('"' + $Destination + '"'))
        $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
            -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        exit $child.ExitCode
    }
    $archive = Join-Path $PSScriptRoot 'payload.zip'
    $portable = !(Test-Path -LiteralPath $archive) -and
        (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'PCManager.exe')) -and
        (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'www\quick.html'))
    if (!(Test-Path -LiteralPath $archive) -and !$portable) {
        throw 'No setup payload or extracted PC Manager application was found beside this installer.'
    }
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { throw '.NET 8 Desktop Runtime is missing. Install it from https://dotnet.microsoft.com/download/dotnet/8.0, then run this setup again.' }
    $runtimes = @(& $dotnet --list-runtimes)
    if ($LASTEXITCODE -ne 0 -or !(@($runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' }).Count) -or
        !(@($runtimes | Where-Object { $_ -match '^Microsoft.WindowsDesktop.App 8\.' }).Count)) {
        throw '.NET 8 Desktop Runtime is missing. Install it from https://dotnet.microsoft.com/download/dotnet/8.0, then run this setup again.'
    }
    $webViewClient = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $webViewKeys = @(
        ('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + $webViewClient),
        ('HKCU:\Software\Microsoft\EdgeUpdate\Clients\' + $webViewClient)
    )
    $webViewPresent = @($webViewKeys | Where-Object {
        $version = (Get-ItemProperty -LiteralPath $_ -Name pv -ErrorAction SilentlyContinue).pv
        $version -and $version -ne '0.0.0.0'
    }).Count -gt 0
    if (!$webViewPresent) {
        throw 'Microsoft Edge WebView2 Runtime is missing. Install it from https://developer.microsoft.com/microsoft-edge/webview2/, then run this setup again.'
    }
    $destinationFull = [IO.Path]::GetFullPath($Destination)
    $windowsRoot = [IO.Path]::GetFullPath($env:SystemRoot).TrimEnd('\')
    $volumeRoot = [IO.Path]::GetPathRoot($destinationFull).TrimEnd('\')
    if ($destinationFull.TrimEnd('\') -eq $volumeRoot -or
        $destinationFull.TrimEnd('\').Equals($windowsRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $destinationFull.StartsWith($windowsRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $destinationFull.TrimEnd('\') -in @([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'),
            [IO.Path]::GetFullPath(${env:ProgramFiles(x86)}).TrimEnd('\'),
            [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\'))) {
        throw 'Choose a dedicated application folder, not a drive root or Windows system directory.'
    }
    $ancestor = $destinationFull
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a redirected installation path: $ancestor"
        }
        $parent = Split-Path -Parent $ancestor
        if (!$parent -or $parent -eq $ancestor) { break }
        $ancestor = $parent
    }
    if (Test-Path -LiteralPath $destinationFull) {
        $existing = @(Get-ChildItem -LiteralPath $destinationFull -Force)
        if ($existing.Count) {
            $existingReceipt = Join-Path $destinationFull 'install-receipt.json'
            if (!(Test-Path -LiteralPath $existingReceipt) -or
                (Get-Content -LiteralPath $existingReceipt -Raw | ConvertFrom-Json).product -ne 'XiaomiAIManager') {
                throw 'The selected folder is not empty and is not an existing PC Manager installation.'
            }
        }
    }
    $exe = Join-Path $destinationFull 'PCManager.exe'
    if (!$NoStartup) {
        $taskName = 'XiaomiAIManager_' + $sid
        $oldTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($oldTask) {
            $actions = @($oldTask.Actions | Where-Object { $_.Execute })
            if ($actions.Count -ne 1 -or [IO.Path]::GetFileName($actions[0].Execute.Trim('"')) -ne 'PCManager.exe') {
                throw 'The existing startup task has an unexpected action. Installation stopped without changing it.'
            }
            $previousTaskXml = Export-ScheduledTask -TaskName $taskName
            $backupRoot = Join-Path $env:LOCALAPPDATA 'XiaomiAIManager\installer-backups'
            New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
            Set-Content -LiteralPath (Join-Path $backupRoot ('startup-task-' + (Get-Date -Format 'yyyyMMddTHHmmssfff') + '.xml')) `
                -Value $previousTaskXml -Encoding Unicode
            $oldExe = $actions[0].Execute.Trim('"')
            Disable-ScheduledTask -TaskName $taskName | Out-Null
            if (Test-Path -LiteralPath $oldExe) {
                $oldPids = @(Get-Process -Name PCManager -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
                Start-Process -FilePath $oldExe -ArgumentList '--quit' -WindowStyle Hidden -Wait | Out-Null
                for ($attempt = 0; $attempt -lt 40; $attempt++) {
                    $active = @($oldPids | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
                    if ($active.Count -eq 0) { break }
                    Start-Sleep -Milliseconds 250
                }
                if ($active.Count -ne 0) { throw 'The old PC Manager did not exit cleanly. Its startup task will be restored.' }
            }
        }
    }
    if (Test-Path -LiteralPath $exe) {
        # Ask only the installed copy to exit. An unrelated developer or test resident is left alone.
        $old = Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -Wait -PassThru
        if ($old.ExitCode -ne 0) { throw 'The existing installed copy did not close cleanly.' }
        Start-Sleep -Seconds 2
    }
    New-Item -ItemType Directory -Force -Path $destinationFull | Out-Null
    if ($portable) {
        if ($destinationFull.Equals([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Choose an installation directory different from the extracted portable folder.'
        }
        Get-ChildItem -LiteralPath $PSScriptRoot -Force |
            Where-Object { $_.Name -notin @('test-data','results') } |
            Copy-Item -Destination $destinationFull -Recurse -Force
    } else {
        Expand-Archive -LiteralPath $archive -DestinationPath $destinationFull -Force
    }
    if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath (Join-Path $destinationFull 'www\quick.html')) -or
        !(Test-Path -LiteralPath (Join-Path $destinationFull 'tools\oem-service.ps1'))) {
        throw 'The archive did not extract a complete PC Manager application.'
    }
    $receipt = Join-Path $destinationFull 'install-receipt.json'
    [pscustomobject]@{product='XiaomiAIManager';scope=$Scope;ownerSid=$sid;installedAt=(Get-Date).ToString('o'); executable=$exe;
        sourceSha256=$(if ($portable) { (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'PCManager.dll')).Hash } else { (Get-FileHash -LiteralPath $archive).Hash });
        startupRequested=(!$NoStartup); dataDirectory=(Join-Path $env:LOCALAPPDATA 'XiaomiAIManager')} |
        ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
    if (!$NoStartup) {
        $oemScript = Join-Path $destinationFull 'tools\oem-service.ps1'
        $intent = Join-Path $env:LOCALAPPDATA ('XiaomiAIManager\service-state\' + $sid + '\oem-service-isolation.json')
        $wasIsolated = (Test-Path -LiteralPath $intent) -and
            ((Get-Content -LiteralPath $intent -Raw | ConvertFrom-Json).enabled -eq $true)
        $oemWasIsolated = $wasIsolated
        & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $oemScript -Action Disable
        if ($LASTEXITCODE -ne 0) { throw 'Xiaomi OEM isolation failed. PC Manager startup was not registered.' }
        # Register first with a short-lived process, then let Task Scheduler start the resident independently.
        $registration = Start-Process -FilePath $exe -ArgumentList '--register-startup' -WorkingDirectory $destinationFull -WindowStyle Hidden -Wait -PassThru
        if ($registration.ExitCode -ne 0) { throw 'Windows refused the PC Manager startup task.' }
        $registered = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if ($task -and @($task.Actions | Where-Object { $_.Execute -eq $exe }).Count -gt 0) { $registered = $true; break }
            Start-Sleep -Seconds 1
        }
        if (!$registered) { throw 'Files installed, but the startup task was not confirmed. Run PC Manager from its install folder and approve the administrator prompt.' }
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') { break }
            Start-Sleep -Seconds 1
        }
        if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') { throw 'PC Manager installed, but its startup task did not start the resident.' }
        $startupConfirmed = $true
        $programs = if ($Scope -eq 'AllUsers') { [Environment]::GetFolderPath('CommonPrograms') }
            else { [Environment]::GetFolderPath('Programs') }
        $startMenu = Join-Path $programs 'PC Manager.lnk'
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
        $shortcut.TargetPath = $exe
        $shortcut.Arguments = '--manager'
        $shortcut.WorkingDirectory = $destinationFull
        $shortcut.IconLocation = $exe + ',0'
        $shortcut.Save()
        $uninstaller = Join-Path $destinationFull 'Uninstall PC Manager.ps1'
        if (!(Test-Path -LiteralPath $uninstaller)) { throw 'The packaged uninstaller is missing.' }
        $uninstallHive = if ($Scope -eq 'AllUsers') { 'HKLM:' } else { 'HKCU:' }
        $uninstallKey = $uninstallHive + '\Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiAIManager'
        New-Item -Path $uninstallKey -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'PC Manager' -PropertyType String -Force | Out-Null
        $installedVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
        if (!$installedVersion) { throw 'The installed executable does not report a product version.' }
        New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $installedVersion -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $destinationFull -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name UninstallString `
            -Value ('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstaller + '"') `
            -PropertyType String -Force | Out-Null
    }
    Show-Result ("PC Manager installed at: " + $destinationFull + "`n" +
        $(if ($NoStartup) { 'Isolated file test completed; startup was not changed.' } else { 'It will run in the tray at sign-in. Open it with the tray icon.' }) +
        "`nSettings and artwork remain in " + (Join-Path $env:LOCALAPPDATA 'XiaomiAIManager') +
        $(if ($NoStartup) { '' } else { "`nXiaomi's original popup service was reversibly disabled." }))
    exit 0
} catch {
    if (!$oemWasIsolated -and !$startupConfirmed -and $oemScript) {
        try { & (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $oemScript -Action Restore | Out-Null }
        catch { Write-Host 'OEM restoration after failed installation needs manual review; the recovery record remains in LocalAppData\XiaomiAIManager\service-state.' }
    }
    if ($previousTaskXml -and !$startupConfirmed -and $taskName) {
        try { Register-ScheduledTask -TaskName $taskName -Xml $previousTaskXml -Force | Out-Null }
        catch { Write-Host 'The previous startup task could not be restored automatically; its exported XML remains in LocalAppData\XiaomiAIManager\installer-backups.' }
    }
    Show-Result $_.Exception.Message $true
    exit 1
}

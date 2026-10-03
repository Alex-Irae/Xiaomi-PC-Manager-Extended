# Purpose: remove a verified PC Manager installation from its chosen directory.
# Dependencies: Windows PowerShell, installed tools/oem-service.ps1, administrator approval.
# Outputs: removes the app, startup, API firewall rule, shortcuts, registry entry and app-owned data.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File "Uninstall PC Manager.ps1" [-RestoreXiaomi] [-NoPrompt]
param([string]$ExpectedSid = '', [switch]$RestoreXiaomi, [switch]$NoPrompt)
$ErrorActionPreference = 'Stop'

$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedSid -and $ExpectedSid -ne $currentSid) {
    throw 'The administrator prompt selected a different Windows account. Uninstall while signed in to an administrator account.'
}
if (!$NoPrompt) {
    Add-Type -AssemblyName System.Windows.Forms
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Uninstall PC Manager'
    $form.StartPosition = 'CenterScreen'
    $form.ClientSize = New-Object System.Drawing.Size(460, 155)
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $label = New-Object System.Windows.Forms.Label
    $label.Text = 'Remove PC Manager, its startup task, settings and cache?'
    $label.SetBounds(18, 18, 420, 24)
    $check = New-Object System.Windows.Forms.CheckBox
    $check.Text = "Restore Xiaomi's original service and popup"
    $check.Checked = [bool]$RestoreXiaomi
    $check.SetBounds(18, 55, 420, 25)
    $remove = New-Object System.Windows.Forms.Button
    $remove.Text = 'Uninstall'
    $remove.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $remove.SetBounds(259, 105, 85, 30)
    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $cancel.SetBounds(354, 105, 85, 30)
    $form.Controls.AddRange(@($label, $check, $remove, $cancel))
    $form.AcceptButton = $remove
    $form.CancelButton = $cancel
    $choice = $form.ShowDialog()
    $RestoreXiaomi = $check.Checked
    $form.Dispose()
    if ($choice -ne [System.Windows.Forms.DialogResult]::OK) { exit 2 }
}
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$isAdmin) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-ExpectedSid', $currentSid, '-NoPrompt')
    if ($RestoreXiaomi) { $args += '-RestoreXiaomi' }
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList $args
    exit $child.ExitCode
}

$installRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$receiptPath = Join-Path $installRoot 'install-receipt.json'
if (!(Test-Path -LiteralPath $receiptPath)) { throw 'The PC Manager installation receipt is missing; no files were removed.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
$exe = Join-Path $installRoot 'PCManager.exe'
if ($receipt.product -ne 'XiaomiAIManager' -or $receipt.scope -notin @('AllUsers','CurrentUser') -or
    $receipt.ownerSid -ne $currentSid -or
    !$receipt.executable.Equals($exe, [StringComparison]::OrdinalIgnoreCase) -or
    $installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\') -or
    $installRoot.Equals([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $installRoot.Equals([IO.Path]::GetFullPath(${env:ProgramFiles(x86)}).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installation receipt does not match this dedicated PC Manager directory.'
}
$ancestor = $installRoot
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and
        ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a redirected installation path: $ancestor"
    }
    $parent = Split-Path -Parent $ancestor
    if (!$parent -or $parent -eq $ancestor) { break }
    $ancestor = $parent
}
$dataRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'XiaomiAIManager'))
$apiRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'XiaomiAIManager'))
foreach ($root in @($installRoot, $dataRoot, $apiRoot)) {
    if ((Test-Path -LiteralPath $root) -and ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a redirected application directory: $root"
    }
}
$taskName = 'XiaomiAIManager_' + $currentSid
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$taskWasEnabled = $task -and $task.State -ne 'Disabled'
$taskWasRunning = $task -and $task.State -eq 'Running'
$completed = $false
if ($task) {
    if (@($task.Actions | Where-Object { $_.Execute -eq $exe }).Count -ne 1) {
        throw 'The startup task does not point to this installation. No task or files were removed.'
    }
    Disable-ScheduledTask -TaskName $taskName | Out-Null
}
try {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='PCManager.exe'" |
        Where-Object { $_.ExecutablePath -eq $exe })
    if ($running.Count -or $taskWasRunning) {
        Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -Wait
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            $remaining = @($running | Where-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
            $stillRunning = (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue).State -eq 'Running'
            if (!$remaining.Count -and !$stillRunning) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($remaining.Count -or $stillRunning) {
            throw 'PC Manager did not exit cleanly; no installed files were removed.'
        }
    }

    $intent = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) `
        ('XiaomiAIManager\service-state\' + $currentSid + '\oem-service-isolation.json')
    if ($RestoreXiaomi -and (Test-Path -LiteralPath $intent)) {
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
    $programs = if ($receipt.scope -eq 'AllUsers') { [Environment]::GetFolderPath('CommonPrograms') }
        else { [Environment]::GetFolderPath('Programs') }
    $startMenu = Join-Path $programs 'PC Manager.lnk'
    if (Test-Path -LiteralPath $startMenu) {
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
        if ($shortcut.TargetPath -eq $exe) { Remove-Item -LiteralPath $startMenu }
    }
    $uninstallHive = if ($receipt.scope -eq 'AllUsers') { 'HKLM:' } else { 'HKCU:' }
    $uninstallKey = $uninstallHive + '\Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiAIManager'
    if ((Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue).InstallLocation -eq $installRoot) {
        Remove-Item -LiteralPath $uninstallKey
    }
    # WebView2 may release files slightly after the resident exits. Verify each app-owned root is gone.
    foreach ($root in @($apiRoot, $dataRoot, $installRoot)) {
        for ($attempt = 0; $attempt -lt 10 -and (Test-Path -LiteralPath $root); $attempt++) {
            if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to remove a redirected application directory: $root"
            }
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $root) { Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path -LiteralPath $root) { throw "Could not remove application directory: $root" }
    }
    $defaultParent = if ($receipt.scope -eq 'AllUsers') { Join-Path $env:ProgramFiles 'Xiaomi Revamp' }
        else { Join-Path $env:LOCALAPPDATA 'Programs\Xiaomi Revamp' }
    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $installRoot)).TrimEnd('\')
    if ($parent.Equals([IO.Path]::GetFullPath($defaultParent).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $parent) -and !(Get-ChildItem -LiteralPath $parent -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $parent
    }
    $completed = $true
    Write-Host ('PASS PC Manager removed; Xiaomi OEM service ' + $(if ($RestoreXiaomi) { 'restored to its saved state.' } else { 'left in its current state.' }))
} finally {
    if (!$completed -and $taskWasEnabled) { Enable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null }
}


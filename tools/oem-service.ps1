# Purpose: reversibly isolate Xiaomi's UI watchdog, or restore its saved service state.
# Dependencies: Windows PowerShell, administrator access, installed MiDeviceService.
# Outputs: original service JSON, isolation intent and timestamped action records; no files deleted.
# Command: powershell -NoProfile -File tools/oem-service.ps1 -Action Disable (or Restore/Tools/Status).
param([ValidateSet('Disable','Restore','Tools','Status')][string]$Action = 'Status')
$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin -and $Action -ne 'Status') {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Action',$Action)
    $child.WaitForExit()
    if ($child.ExitCode -eq 0) { Write-Host ('Completed ' + $Action + '. Recovery records are in LocalAppData\XiaomiAIManager\service-state.') }
    else { Write-Error -Message 'The service action failed. Original records are preserved in LocalAppData\XiaomiAIManager\service-state.' -ErrorAction Continue }
    exit $child.ExitCode
}
$scriptParent = Split-Path -Parent $PSScriptRoot
$deploymentRoot = if (Test-Path -LiteralPath (Join-Path $scriptParent 'XiaomiAIManager.exe')) { $scriptParent } else { Join-Path $scriptParent 'app' }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$dataRoot = Join-Path $env:LOCALAPPDATA ('XiaomiAIManager\service-state\' + $sid)
$legacyRoot = Join-Path $deploymentRoot ('service-state\' + $sid)
if (Test-Path -LiteralPath $legacyRoot) {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $legacyRoot -File) {
        $target = Join-Path $dataRoot $file.Name
        if (!(Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
}
if ($Action -ne 'Status') {
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    Start-Transcript -Path (Join-Path $dataRoot ('oem-service-' + (Get-Date -Format 'yyyyMMddTHHmmssfff') + '-' + $Action + '.log')) | Out-Null
}
$backupPath = Join-Path $dataRoot 'oem-service-original.json'
$intentPath = Join-Path $dataRoot 'oem-service-isolation.json'
$serviceName = 'MiDeviceService'
$service = Get-CimInstance Win32_Service -Filter "Name='MiDeviceService'"
if (!$service) { throw 'MiDeviceService is not installed.' }
$image = $service.PathName.Trim('"')
$expectedRoot = Join-Path ${env:ProgramFiles(x86)} 'Timi Personal Computing\MiService'
if (!$image.StartsWith($expectedRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($image) -ne 'MiDeviceService.exe') { throw 'Unexpected service executable; no change made.' }
if ($Action -eq 'Status') { $service | Select-Object Name,State,StartMode,PathName | ConvertTo-Json; exit 0 }
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
if (!(Test-Path -LiteralPath $backupPath)) {
    if ($Action -eq 'Restore') { throw 'No saved original service state exists; no guessed restoration made.' }
    $delayed = (Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $serviceName) -Name DelayedAutoStart -ErrorAction SilentlyContinue).DelayedAutoStart
    @{name=$serviceName;image=$image;startMode=$service.StartMode;wasRunning=($service.State -eq 'Running');delayedAutoStart=($delayed -eq 1);savedAt=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $backupPath -Encoding UTF8
}
$original = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
if ($original.name -ne $serviceName -or $original.image -ne $image) { throw 'The installed service changed since the backup; original state preserved for manual review.' }
function Set-Startup([string]$value) {
    & (Join-Path $env:SystemRoot 'System32\sc.exe') config $serviceName 'start=' $value | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Service startup change failed.' }
}
$originalStartup = switch ($original.startMode) { 'Auto' {if($original.delayedAutoStart){'delayed-auto'}else{'auto'}} 'Manual' {'demand'} 'Disabled' {'disabled'} default {throw 'Unsupported saved startup mode.'} }
$sc = Join-Path $env:SystemRoot 'System32\sc.exe'
$sddl = @(& $sc sdshow $serviceName) | Where-Object { $_.Trim().StartsWith('D:') } | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or !$sddl) { throw 'Could not preserve the service permissions.' }
$sddl = $sddl.Trim()
$permissionsPath = Join-Path $dataRoot 'oem-service-permissions.sddl'
if (!(Test-Path -LiteralPath $permissionsPath)) { Set-Content -LiteralPath $permissionsPath -Value $sddl -Encoding ASCII }
$savedSddl = (Get-Content -LiteralPath $permissionsPath -Raw).Trim()
# The OEM explicitly denies CHANGE_CONFIG and STOP to Everyone/System, including administrators.
# Temporarily remove only these denied bits, preserving every other ACE, then restore the exact SDDL.
$descriptor = New-Object Security.AccessControl.RawSecurityDescriptor $savedSddl
for ($index=$descriptor.DiscretionaryAcl.Count-1; $index -ge 0; $index--) {
    $ace=$descriptor.DiscretionaryAcl[$index]
    if ($ace.AceType -eq [Security.AccessControl.AceType]::AccessDenied -and $ace.SecurityIdentifier.Value -in @('S-1-1-0','S-1-5-18')) {
        $remaining=$ace.AccessMask -band (-bnot 0x22)
        if ($remaining -eq 0) { $descriptor.DiscretionaryAcl.RemoveAce($index) } else { $ace.AccessMask=$remaining }
    }
}
$temporary = $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::Access)
if ($sddl -ne $savedSddl -and $sddl -ne $temporary) { throw 'Service permissions changed since the backup; review required before further changes.' }
# An interrupted previous operation can leave our exact temporary ACL; restore from the original.
$sddl = $savedSddl
& $sc sdset $serviceName $temporary | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Windows refused temporary service permissions; original state unchanged.' }
try {
if ($Action -eq 'Disable') {
    Set-Startup 'disabled'
    try { Stop-Service -Name $serviceName -ErrorAction Stop }
    catch { Set-Startup $originalStartup; throw }
    # Limit UI cleanup to this session and the verified installed manager directory.
    $oemDirectories = @()
    foreach ($root in @((Join-Path $env:ProgramFiles 'MI\XiaomiPCManager'),(Join-Path ${env:ProgramFiles(x86)} 'MI\XiaomiPCManager'))) {
        if (!(Test-Path -LiteralPath $root)) { continue }
        $oemDirectories += $root
        $oemDirectories += @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } | ForEach-Object { $_.FullName })
    }
    $suppliedRoot = Split-Path -Parent (Split-Path -Parent $deploymentRoot)
    if ([IO.Path]::GetFileName($suppliedRoot) -eq 'XiaomiPCManager') {
        $oemDirectories += @(Get-ChildItem -LiteralPath $suppliedRoot -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'XiaomiPcManager.exe')) } | ForEach-Object { $_.FullName })
    }
    $hostTask = Get-ScheduledTask -TaskName 'XiaomiPCHostTask' -ErrorAction SilentlyContinue
    if ($hostTask -and $hostTask.State -ne 'Disabled') {
        $actions = @($hostTask.Actions | Where-Object { $_.Execute })
        $expectedHosts = @($oemDirectories | ForEach-Object { Join-Path $_ 'XiaomiPcHost.exe' })
        if ($actions.Count -ne 1 -or $actions[0].Execute.Trim('"') -notin $expectedHosts) {
            throw 'XiaomiPCHostTask has an unexpected action; it was not changed.'
        }
        @{execute=$actions[0].Execute.Trim('"');wasEnabled=$true} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $dataRoot 'oem-host-task.json') -Encoding UTF8
        Disable-ScheduledTask -TaskName 'XiaomiPCHostTask' | Out-Null
    }
    $runPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    $run = Get-Item -LiteralPath $runPath
    $expectedLaunchers = @($oemDirectories | ForEach-Object { Join-Path $_ 'Launch.exe' })
    $savedRunEntries = @()
    foreach ($name in $run.GetValueNames()) {
        $command = [string]$run.GetValue($name)
        if (@($expectedLaunchers | Where-Object { $command.Trim() -in @(($_ + ' --AutoRun=1'), ('"' + $_ + '" --AutoRun=1')) }).Count -eq 0) { continue }
        $savedRunEntries += @{name=$name;command=$command;kind=[string]$run.GetValueKind($name)}
    }
    if ($savedRunEntries.Count) {
        ConvertTo-Json -InputObject $savedRunEntries | Set-Content -LiteralPath (Join-Path $dataRoot 'oem-run-entry.json') -Encoding UTF8
    }
    foreach ($entry in $savedRunEntries) {
        Remove-ItemProperty -LiteralPath $runPath -Name $entry.name
    }
    $session = (Get-Process -Id $PID).SessionId
    if (-not ('XiaomiNativeProcessImage' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class XiaomiNativeProcessImage {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    public static string Read(int id) {
        var handle=OpenProcess(0x1000,false,id);
        if(handle==IntPtr.Zero) return null;
        try { var path=new StringBuilder(32768); int length=path.Capacity; return QueryFullProcessImageNameW(handle,0,path,ref length) ? path.ToString() : null; }
        finally { CloseHandle(handle); }
    }
}
'@
    }
    foreach ($process in Get-Process OSDLauncher,OSDUtility,XiaomiPcManager,XiaomiPcHost -ErrorAction SilentlyContinue) {
        if ($process.SessionId -ne $session) { continue }
        $expectedImages = @($oemDirectories | ForEach-Object { Join-Path $_ ($process.ProcessName + '.exe') })
        $actualImage = [XiaomiNativeProcessImage]::Read($process.Id)
        if ($actualImage -in $expectedImages) { Stop-Process -Id $process.Id -Force; Write-Host ('Stopped verified OEM process ' + $process.Id + ': ' + $actualImage) }
        elseif (!$actualImage) { throw ('Could not verify OEM process image for PID ' + $process.Id + '; no unverified process was stopped.') }
    }
    @{enabled=$true;time=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $intentPath -Encoding UTF8
} elseif ($Action -eq 'Tools') {
    Set-Startup 'demand'
    Start-Service -Name $serviceName
} else {
    Set-Startup $originalStartup
    if ($original.wasRunning) { Start-Service -Name $serviceName } else { Stop-Service -Name $serviceName }
    $taskRecord = Join-Path $dataRoot 'oem-host-task.json'
    if (Test-Path -LiteralPath $taskRecord) {
        $savedTask = Get-Content -LiteralPath $taskRecord -Raw | ConvertFrom-Json
        $hostTask = Get-ScheduledTask -TaskName 'XiaomiPCHostTask' -ErrorAction Stop
        if (@($hostTask.Actions | Where-Object { $_.Execute.Trim('"') -eq $savedTask.execute }).Count -ne 1) {
            throw 'XiaomiPCHostTask changed since isolation; its original state was not guessed.'
        }
        if ($savedTask.wasEnabled) { Enable-ScheduledTask -TaskName 'XiaomiPCHostTask' | Out-Null }
    }
    $runRecord = Join-Path $dataRoot 'oem-run-entry.json'
    if (Test-Path -LiteralPath $runRecord) {
        $savedRunEntries = @(Get-Content -LiteralPath $runRecord -Raw | ConvertFrom-Json)
        $runPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
        foreach ($savedRun in $savedRunEntries) {
            if ($null -eq (Get-Item -LiteralPath $runPath).GetValue($savedRun.name)) {
                New-ItemProperty -LiteralPath $runPath -Name $savedRun.name -Value $savedRun.command -PropertyType $savedRun.kind | Out-Null
            }
        }
    }
    @{enabled=$false;time=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $intentPath -Encoding UTF8
}
} finally {
    & $sc sdset $serviceName $sddl | Out-Host
    if ($LASTEXITCODE -ne 0) { throw ('Original service permissions could not be restored. Saved SDDL: ' + $permissionsPath) }
}
$verifiedSddl = @(& $sc sdshow $serviceName) | Where-Object { $_.Trim().StartsWith('D:') } | Select-Object -First 1
if ($LASTEXITCODE -ne 0 -or !$verifiedSddl -or $verifiedSddl.Trim() -ne $savedSddl) { throw 'Original service permissions did not pass privileged readback.' }
if ($Action -eq 'Disable') {
    $residentExe = Join-Path $deploymentRoot 'XiaomiAIManager.exe'
    if (Test-Path -LiteralPath $residentExe) {
        $relay = Start-Process -FilePath $residentExe -ArgumentList '--reapply-policies' -WindowStyle Hidden -PassThru
        if (!$relay.WaitForExit(5000)) { Write-Warning 'Service is isolated; resident policy notification is still pending.' }
    }
}
$record = Join-Path $dataRoot ('oem-service-' + (Get-Date -Format 'yyyyMMddTHHmmssfff') + '-' + $Action + '.json')
Get-CimInstance Win32_Service -Filter "Name='MiDeviceService'" | Select-Object Name,State,StartMode,PathName | ConvertTo-Json | Set-Content -LiteralPath $record -Encoding UTF8
Write-Host ('Completed ' + $Action + '; saved original state: ' + $backupPath)

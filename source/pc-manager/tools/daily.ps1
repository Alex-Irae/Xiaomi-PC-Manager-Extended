# Purpose: deploy the built resident, preserve and disable competing UI startup entries, register logon startup.
# Dependencies: Windows PowerShell, administrator approval, existing app/PCManager.exe build.
# Outputs: preserved startup records and deployment logs under .test-environment; per-user resident settings/task.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/daily.ps1 -Install
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dailyRoot = Join-Path $projectRoot 'app'
$exe = Join-Path $dailyRoot 'PCManager.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build the app directory first.' }
if (!$Install) { Start-Process -FilePath $exe -ArgumentList '--toggle' -WorkingDirectory $dailyRoot -WindowStyle Hidden; return }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) {
    Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Install')
    return
}
$logRoot = Join-Path $projectRoot ('.test-environment\deployments\' + (Get-Date -Format 'yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
Start-Transcript -Path (Join-Path $logRoot 'deployment.txt') | Out-Null
try {
    $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
    $oemRoots = @((Join-Path $env:ProgramFiles 'MI\XiaomiPCManager'),(Join-Path ${env:ProgramFiles(x86)} 'MI\XiaomiPCManager')) | Where-Object { Test-Path -LiteralPath $_ }
    $launchers = @($oemRoots | ForEach-Object { Join-Path $_ 'Launch.exe' } | Where-Object { Test-Path -LiteralPath $_ })
    $run = Get-Item -LiteralPath $runKey
    foreach ($name in $run.GetValueNames()) {
        $command = [string]$run.GetValue($name)
        if (@($launchers | Where-Object { $command.Trim() -in @(($_ + ' --AutoRun=1'), ('"' + $_ + '" --AutoRun=1')) }).Count -gt 0) {
            [pscustomobject]@{path=$runKey;name=$name;command=$command;kind=[string]$run.GetValueKind($name)} | ConvertTo-Json | Set-Content (Join-Path $logRoot 'oem-run-entry.json') -Encoding UTF8
            Remove-ItemProperty -LiteralPath $runKey -Name $name
            Write-Host 'Preserved and disabled Xiaomi UI logon launch.'
        }
    }
    foreach ($task in (Get-ScheduledTask | Where-Object {$_.TaskName -eq 'XiaomiPCHostTask' -or $_.TaskName -like 'XiControl_*'})) {
        $executables = @($task.Actions | Where-Object {$_.Execute} | ForEach-Object { [IO.Path]::GetFileName($_.Execute.Trim('"')) })
        $ours = ($task.TaskName -eq 'XiaomiPCHostTask' -and $executables -contains 'XiaomiPcHost.exe') -or
                ($task.TaskName -like 'XiControl_*' -and @($executables | Where-Object { $_ -like 'XiControl*.exe' }).Count -gt 0)
        if (!$ours -or $task.State -eq 'Disabled') { continue }
        Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Set-Content (Join-Path $logRoot ($task.TaskName + '.xml')) -Encoding Unicode
        Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Out-Null
        Write-Host ('Preserved and disabled competing task: ' + $task.TaskName)
    }
    # Restart only the exact project app and verified OEM UI processes, leaving firmware services alone.
    $allowedOem = @($oemRoots | ForEach-Object {
        $root = $_
        @($root) + @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' } | ForEach-Object { $_.FullName })
    } | ForEach-Object { $directory = $_; @('XiaomiPcManager.exe','XiaomiPcHost.exe') | ForEach-Object { Join-Path $directory $_ } })
    foreach ($process in Get-Process PCManager,XiaomiPcManager,XiaomiPcHost -ErrorAction SilentlyContinue) {
        $image = $process.MainModule.FileName
        $owned = $process.ProcessName -eq 'PCManager' -and $image.StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase)
        if (!$owned -and $allowedOem -notcontains $image) { continue }
        if ($owned) {
            $args = @('--quit'); if ($image -ne $exe) { $args = @('--test','--quit') }
            # Older test builds lack --quit; all settings are already saved on each explicit change.
            if ($image -eq $exe) { Start-Process -FilePath $exe -ArgumentList $args -WindowStyle Hidden; Start-Sleep -Seconds 2 }
        }
        if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $process.Id -Force }
        Write-Host ('Stopped verified UI: ' + $image)
    }
    $dataRoot = Join-Path $env:LOCALAPPDATA 'XiaomiAIManager'
    $staging = Join-Path $projectRoot '.test-environment\daily-next'
    if (Test-Path -LiteralPath (Join-Path $staging 'PCManager.exe')) {
        Get-ChildItem -LiteralPath $staging | Copy-Item -Destination $dailyRoot -Recurse -Force
        Write-Host 'Updated the daily app from the staged build.'
    }
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $settings = Join-Path $dataRoot 'settings.json'
    $testSettings = Join-Path $projectRoot '.test-environment\artifacts\bin\XiaomiAIManager\release\test-data\settings.json'
    if (!(Test-Path -LiteralPath $settings) -and (Test-Path -LiteralPath $testSettings)) { Copy-Item -LiteralPath $testSettings -Destination $settings }
    $started = Start-Process -FilePath $exe -ArgumentList '--install-startup' -WorkingDirectory $dailyRoot -WindowStyle Hidden -PassThru
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $taskName = 'XiaomiAIManager_' + $sid
    for ($attempt=0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Seconds 1
        $registered = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($registered -and @($registered.Actions | Where-Object {$_.Execute -eq $exe}).Count -gt 0) { break }
    }
    if (!$registered -or @($registered.Actions | Where-Object {$_.Execute -eq $exe}).Count -eq 0) { throw 'The resident task was not confirmed at the expected executable.' }
    [pscustomobject]@{time=(Get-Date).ToString('o');resident_id=$started.Id;executable=$exe;task=$taskName;state=[string]$registered.State;startup_confirmed=$true;preserved_entries=$logRoot} | ConvertTo-Json | Set-Content (Join-Path $logRoot 'result.json') -Encoding UTF8
    Write-Host 'Resident launched and per-user logon task confirmed.'
} catch { $_ | Out-String | Set-Content (Join-Path $logRoot 'error.txt'); throw }
finally { Stop-Transcript | Out-Null }



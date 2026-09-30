# Purpose: verify clean-exit recovery through the resident's existing per-user scheduled task.
# Dependencies: Windows PowerShell, administrator access and the registered daily resident.
# Outputs: a fresh numbered results folder with config, task XML and restart result; no files deleted.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-restart.ps1
$ErrorActionPreference = 'Stop'
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'))
    exit $child.ExitCode
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $projectRoot 'app\XiaomiAIManager.exe'
$taskName = 'XiaomiAIManager_' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$xml = Export-ScheduledTask -TaskName $taskName
if (!$xml.Contains('<Repetition>') -or !$xml.Contains('<Interval>PT1M</Interval>') -or !$xml.Contains($exe) -or !$xml.Contains('<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>')) { throw 'Expected resident task recovery configuration is missing.' }
$before = @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
if ($before.Count -ne 1) { throw 'Expected exactly one verified daily resident.' }
$highest = 0
Get-ChildItem (Join-Path $projectRoot 'results') -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $highest = [Math]::Max($highest,[int]$Matches[1]) } }
$runRoot = Join-Path $projectRoot ('results\{0:D3}_{1}_restart' -f ($highest+1),(Get-Date -Format yyyyMMddTHHmmssfff))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$xml | Set-Content (Join-Path $runRoot 'task.xml') -Encoding Unicode
@{seed=$null;oldPid=$before[0].ProcessId;executable=$exe;assemblySha256=(Get-FileHash (Join-Path $projectRoot 'app\XiaomiAIManager.dll')).Hash;protocol='Clean quit, then passive task recovery observation up to 90 seconds. No reboot, failure injection or hardware writes.'} | ConvertTo-Json | Set-Content (Join-Path $runRoot 'config.json') -Encoding UTF8
$timer = [Diagnostics.Stopwatch]::StartNew()
$observedExit = $false
try {
    Start-Process -FilePath $exe -ArgumentList '--quit' -WindowStyle Hidden -Wait
    $after = @()
    for ($attempt=0; $attempt -lt 45; $attempt++) {
        $after = @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
        if (!(Get-Process -Id $before[0].ProcessId -ErrorAction SilentlyContinue)) { $observedExit = $true }
        if ($observedExit -and $after.Count -eq 1 -and $after[0].ProcessId -ne $before[0].ProcessId) { break }
        if ($attempt % 5 -eq 0) { Write-Host ('Waiting for task recovery: {0:N1} seconds, old process exited={1}' -f $timer.Elapsed.TotalSeconds,$observedExit) }
        Start-Sleep -Seconds 2
    }
    $pass = $observedExit -and $after.Count -eq 1 -and $after[0].ProcessId -ne $before[0].ProcessId
    @{pass=$pass;oldPid=$before[0].ProcessId;newPid=if($after.Count -eq 1){$after[0].ProcessId}else{$null};elapsedSeconds=$timer.Elapsed.TotalSeconds;residentCount=$after.Count;taskState=(Get-ScheduledTask -TaskName $taskName).State.ToString()} | ConvertTo-Json | Set-Content (Join-Path $runRoot 'summary.json') -Encoding UTF8
    if (!$pass) { throw 'Automatic clean-exit recovery did not pass within 90 seconds.' }
    Write-Host ('PASS clean-exit recovery: ' + $runRoot)
} finally {
    if (!(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" | Where-Object { $_.ExecutablePath -eq $exe })) { Start-ScheduledTask -TaskName $taskName }
}

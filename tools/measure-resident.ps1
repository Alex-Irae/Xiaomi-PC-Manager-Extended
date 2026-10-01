# Purpose: read resident-process CPU/memory and available whole-machine power counters without hardware writes.
# Dependencies: Windows PowerShell/CIM. Outputs: a new results/NNN_timestamp_resident directory, CSV and JSON.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/measure-resident.ps1 -ResidentId 3916 -Seconds 60 -Label before
param([Parameter(Mandatory=$true)][int]$ResidentId, [ValidateRange(10,3600)][int]$Seconds = 60, [string]$Label = 'resident')
$ErrorActionPreference = 'Stop'
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-ResidentId',$ResidentId,'-Seconds',$Seconds,'-Label',('"' + $Label + '"'))
    exit $child.ExitCode
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $projectRoot 'results'
New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
$highest = 0
Get-ChildItem -LiteralPath $resultsRoot -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $highest = [Math]::Max($highest,[int]$Matches[1]) } }
$runRoot = Join-Path $resultsRoot (('{0:D3}_{1}_resident' -f ($highest+1),(Get-Date -Format 'yyyyMMddTHHmmssfff')))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Start-Transcript -Path (Join-Path $runRoot 'measurement.txt') | Out-Null
$resident = Get-Process -Id $ResidentId
if ($resident.ProcessName -ne 'XiaomiAIManager') { throw 'The target must be the XiaomiAIManager process.' }
$started = $resident.StartTime
$logicalCpus = [Environment]::ProcessorCount
$rows = [Collections.Generic.List[object]]::new()
$previous = @{}
$config = [ordered]@{ label=$Label; resident_id=$ResidentId; resident_start=$started; seconds=$Seconds; interval_seconds=3; logical_cpus=$logicalCpus; seed=$null; protocol='Passive manager and descendant WebView2 process sampling. No workload controlled; no hardware writes.'; cpu_definition='100 * CPU-seconds delta / elapsed wall seconds / logical CPU count'; power_definition='Whole CPU package RAPL counter in W, not per-process power or wall consumption'; input='Running Windows process and CIM counters'; version='0.1.0'; baseline_controlled=$false }
$dailyExe = Join-Path $projectRoot 'app\PCManager.exe'
$config.daily_executable = $dailyExe
$config.daily_assembly_sha256 = if (Test-Path -LiteralPath (Join-Path $projectRoot 'app\PCManager.dll')) { (Get-FileHash -LiteralPath (Join-Path $projectRoot 'app\PCManager.dll') -Algorithm SHA256).Hash } else { $null }
$target = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $ResidentId)
$config.resident_executable = $target.ExecutablePath
$config.resident_assembly_sha256 = if ($target.ExecutablePath -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $target.ExecutablePath) 'PCManager.dll'))) { (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $target.ExecutablePath) 'PCManager.dll')).Hash } else { $null }
$taskName = 'XiaomiAIManager_' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) { Export-ScheduledTask -TaskName $taskName | Set-Content (Join-Path $runRoot 'startup-task.xml') -Encoding Unicode }
$oemNames = @('XiaomiPcManager.exe','XiaomiPcHost.exe','OSDUtility.exe','OSDLauncher.exe','XiControl.exe')
@{ time=(Get-Date).ToString('o'); residents=@(Get-CimInstance Win32_Process -Filter "Name='PCManager.exe'" | Select-Object ProcessId,ExecutablePath); taskState=[string]$task.State; oem=@(Get-CimInstance Win32_Process | Where-Object {$oemNames -contains $_.Name} | Select-Object Name,ProcessId,ExecutablePath); service=Get-CimInstance Win32_Service -Filter "Name='MiDeviceService'" | Select-Object Name,State,StartMode } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $runRoot 'resident-check.json') -Encoding UTF8
$config | ConvertTo-Json | Set-Content (Join-Path $runRoot 'config.json') -Encoding UTF8
$clock = [Diagnostics.Stopwatch]::StartNew()
while ($clock.Elapsed.TotalSeconds -le $Seconds) {
    if (!(Get-Process -Id $ResidentId -ErrorAction SilentlyContinue)) { throw 'Resident exited during measurement.' }
    $now = $clock.Elapsed.TotalSeconds
    $inventory = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($ResidentId)
    do { $added=$false; foreach ($item in $inventory) { if ($ids.Contains([int]$item.ParentProcessId) -and $ids.Add([int]$item.ProcessId)) { $added=$true } } } while ($added)
    $cpuPercent=0.0; $memory=0L; $alive=0
    foreach ($processId in $ids) {
        $item = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (!$item) { continue }
        if ($processId -eq $ResidentId -and $item.StartTime -ne $started) { throw 'Resident PID changed identity.' }
        $cpu=[double]$item.CPU; $key=[string]$processId + ':' + $item.StartTime.Ticks
        if ($previous.ContainsKey($key)) { $elapsed=$now-$previous[$key].time; if ($elapsed -gt 0) { $cpuPercent += 100.0*($cpu-$previous[$key].cpu)/$elapsed/$logicalCpus } }
        $previous[$key]=@{time=$now;cpu=$cpu}
        $memory += $item.WorkingSet64; $alive++
    }
    $package=$null; $battery=$null
    try { $meter=Get-CimInstance -Namespace root\cimv2 -ClassName Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter -Filter "Name='RAPL_Package0_PKG'"; if ($meter.Power -gt 0) { $package=[double]$meter.Power/1000.0 } } catch { }
    try { $status=Get-CimInstance -Namespace root\wmi -ClassName BatteryStatus | Select-Object -First 1; if ($status.Charging -and $status.ChargeRate -lt 1000000) { $battery=[double]$status.ChargeRate/1000.0 } elseif ($status.Discharging -and $status.DischargeRate -lt 1000000) { $battery=-[double]$status.DischargeRate/1000.0 } } catch { }
    $rows.Add([pscustomobject]@{seconds=[Math]::Round($now,3);cpu_percent=[Math]::Round($cpuPercent,4);working_set_mib=[Math]::Round($memory/1MB,2);process_count=$alive;cpu_package_w=$package;battery_flow_w=$battery})
    Write-Host ('Sample {0}: CPU {1:N3}% across {2} processes, working set {3:N1} MiB, package {4} W' -f $rows.Count,$cpuPercent,$alive,($memory/1MB),$package)
    if ($alive -eq 0) { throw 'Resident exited during measurement.' }
    Start-Sleep -Seconds 3
}
$rows | Export-Csv (Join-Path $runRoot 'samples.csv') -NoTypeInformation -Encoding UTF8
$valid=@($rows | Select-Object -Skip 1)
$summary=[ordered]@{ label=$Label; samples=$valid.Count; measured_seconds=$clock.Elapsed.TotalSeconds; mean_tree_cpu_percent=($valid | Measure-Object cpu_percent -Average).Average; peak_tree_cpu_percent=($valid | Measure-Object cpu_percent -Maximum).Maximum; mean_working_set_mib=($valid | Measure-Object working_set_mib -Average).Average; mean_whole_cpu_package_w=($valid | Where-Object {$null -ne $_.cpu_package_w} | Measure-Object cpu_package_w -Average).Average; mean_battery_flow_w=($valid | Where-Object {$null -ne $_.battery_flow_w} | Measure-Object battery_flow_w -Average).Average; causal_app_power_w=$null; interpretation='Short observational sample including WebView2. Power includes all workloads and the measurement observer. It cannot establish app-only watts, battery impact or long-term stability.' }
$summary | ConvertTo-Json | Set-Content (Join-Path $runRoot 'summary.json') -Encoding UTF8
Write-Host ('Saved: ' + $runRoot)
$summary | ConvertTo-Json
Stop-Transcript | Out-Null

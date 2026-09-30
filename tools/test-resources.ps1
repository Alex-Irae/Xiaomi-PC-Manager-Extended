# Purpose: compare the hidden resident with all three visible monitor sizes, then restore the saved view.
# Dependencies: Windows PowerShell, the installed daily resident and existing measure-resident.ps1; no new packages.
# Outputs: fresh numbered protocol and measurement folders under results; hardware settings are unchanged.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-resources.ps1 -Seconds 60
param([ValidateRange(10,600)][int]$Seconds = 60)
$ErrorActionPreference = 'Stop'
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Seconds',$Seconds)
    exit $child.ExitCode
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $projectRoot 'app\XiaomiAIManager.exe'
function CaptureResidentPreferences {
    # Read through the running resident, because tool-process profile file views can differ.
    $known = @(Get-ChildItem (Join-Path $projectRoot 'results') -Directory | Select-Object -ExpandProperty Name)
    Start-Process -FilePath $exe -ArgumentList '--validate-ui' -WindowStyle Hidden -Wait
    $capture = $null
    for ($attempt=0; $attempt -lt 45; $attempt++) {
        $capture = Get-ChildItem (Join-Path $projectRoot 'results') -Directory | Where-Object { $_.Name.EndsWith('_ui') -and $_.Name -notin $known } | Sort-Object Name -Descending | Select-Object -First 1
        if ($capture -and (Test-Path -LiteralPath (Join-Path $capture.FullName 'summary.json'))) { break }
        Start-Sleep -Seconds 2
    }
    if (!$capture -or !(Test-Path -LiteralPath (Join-Path $capture.FullName 'summary.json'))) { throw 'Resident preferences capture did not finish.' }
    $captureSummary = Get-Content -LiteralPath (Join-Path $capture.FullName 'summary.json') -Raw | ConvertFrom-Json
    if ($captureSummary.errors.Count -gt 0 -or !$captureSummary.preferencesRestored -or !$captureSummary.diskPreferencesMatch) { throw 'Resident preferences capture/restoration failed.' }
    $captureConfig = Get-Content -LiteralPath (Join-Path $capture.FullName 'config.json') -Raw | ConvertFrom-Json
    if ($captureConfig.assemblySha256 -ne (Get-FileHash (Join-Path $projectRoot 'app\XiaomiAIManager.dll')).Hash) { throw 'Capture does not match the daily build.' }
    return [pscustomobject]@{ preferences=(Get-Content -LiteralPath (Join-Path $capture.FullName 'baseline.json') -Raw | ConvertFrom-Json).preferences; run=$capture.FullName }
}
$captured = CaptureResidentPreferences
$saved = $captured.preferences
$before = @(Get-CimInstance Win32_Process -Filter "Name='XiaomiAIManager.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
if ($before.Count -ne 1) { throw 'Expected exactly one verified daily resident.' }
$highest = 0
Get-ChildItem (Join-Path $projectRoot 'results') -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $highest = [Math]::Max($highest,[int]$Matches[1]) } }
$runRoot = Join-Path $projectRoot ('results\{0:D3}_{1}_resource_protocol' -f ($highest+1),(Get-Date -Format yyyyMMddTHHmmssfff))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Start-Transcript -Path (Join-Path $runRoot 'protocol.log') | Out-Null
@{seed=$null;residentId=$before[0].ProcessId;secondsPerCondition=$Seconds;assemblySha256=(Get-FileHash (Join-Path $projectRoot 'app\XiaomiAIManager.dll')).Hash;savedMonitorView=$saved.XiControl.MonitorView;appearance=$saved.Appearance;preferencesSource=$captured.run;protocol='Resident UI capture with restored preferences, then Hidden, Small, Medium, Large, 3 seconds settling each. No controlled external workload, hardware writes or app-only watt attribution.'} | ConvertTo-Json | Set-Content (Join-Path $runRoot 'config.json') -Encoding UTF8
$runs = [Collections.Generic.List[object]]::new()
$errors = [Collections.Generic.List[string]]::new()
function Relay([string]$command) { Start-Process -FilePath $exe -ArgumentList $command -WindowStyle Hidden -Wait }
try {
    foreach ($view in @('hidden','small','medium','large')) {
        Relay '--hide'
        if ($view -ne 'hidden') { Relay ('--monitor-' + $view) }
        Start-Sleep -Seconds 3
        Write-Host ('Measuring condition: ' + $view)
        & (Join-Path $PSScriptRoot 'measure-resident.ps1') -ResidentId $before[0].ProcessId -Seconds $Seconds -Label ('final-' + $view)
        $latest = Get-ChildItem (Join-Path $projectRoot 'results') -Directory | Sort-Object Name -Descending | Select-Object -First 1
        $runs.Add(@{view=$view;folder=$latest.FullName;summary=(Get-Content (Join-Path $latest.FullName 'summary.json') -Raw | ConvertFrom-Json)})
    }
} catch { $errors.Add($_.Exception.Message); throw }
finally {
    try {
        $restore = switch ($saved.XiControl.MonitorView) { 'mini' {'medium'} 'power' {'small'} default {'large'} }
        Relay ('--monitor-' + $restore)
        Relay '--hide'
        Start-Sleep -Seconds 2
        $afterCapture = CaptureResidentPreferences
        $after = $afterCapture.preferences
        Relay '--hide'
        $viewRestored = $after.XiControl.MonitorView -eq $saved.XiControl.MonitorView
        if (!$viewRestored) { $errors.Add('Saved monitor view was not restored.') }
    } catch { $errors.Add('Restore: ' + $_.Exception.Message) }
    @{time=(Get-Date).ToString('o');runs=$runs.ToArray();errors=$errors.ToArray();monitorViewRestored=$viewRestored;service=(Get-Service MiDeviceService | Select-Object Status,StartType);oem=@(Get-Process -Name XiaomiPcManager,XiaomiPcHost,OSDUtility,OSDLauncher,XiControl -ErrorAction SilentlyContinue | Select-Object Id,ProcessName)} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $runRoot 'summary.json') -Encoding UTF8
    Stop-Transcript | Out-Null
}

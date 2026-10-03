# Purpose: independently read Xiaomi MIFS mode, charging limit and keyboard-light response, without either manager UI.
# Dependencies: Windows PowerShell 5.1, administrator shell and installed Xiaomi WMI interface.
# Output: decoded state, raw response bytes and read-only lock policy in the terminal. No SET requests or policy changes.
# Command (administrator PowerShell): powershell -NoProfile -ExecutionPolicy Bypass -File tools/read-firmware.ps1
param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$taskAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$taskAdministrator) { throw 'Run this read-only checker from an administrator PowerShell window.' }
$taskInterface = Get-WmiObject -Namespace root\wmi -Class MiCommonInterface | Select-Object -First 1
if (!$taskInterface) { throw 'The Xiaomi MiCommonInterface provider is unavailable.' }
function Read-MiState([byte]$command,[byte]$argument=0) {
    $inputBytes = New-Object byte[] 32
    $inputBytes[1]=0xFA; $inputBytes[3]=$command; $inputBytes[4]=$argument
    $parameters = $taskInterface.GetMethodParameters('MiInterface'); $parameters.InData=$inputBytes
    $answer = $taskInterface.InvokeMethod('MiInterface',$parameters,$null)
    return ,([byte[]]$answer.OutData)
}
$taskModeBytes = Read-MiState 0x08
$taskBacklightBytes = Read-MiState 0x12
$taskClassic = $taskModeBytes.Length -gt 4 -and $taskModeBytes[1] -eq 0x80
$taskEcho = $taskModeBytes.Length -gt 2 -and $taskModeBytes[1] -eq 0x08
if (!$taskClassic -and !$taskEcho) { throw ('Unsupported firmware reply: '+[BitConverter]::ToString($taskModeBytes)) }
$taskModeCode = if ($taskClassic) { $taskModeBytes[4] } else { $taskModeBytes[2] }
$taskModeName = switch ($taskModeCode) { 1 {'Legacy balanced'} 2 {'Silent / Quiet'} 3 {'Turbo'} 4 {'Full speed'} 9 {'Smart / Auto'} 10 {'Eco'} default {'Unknown'} }
$taskLimit = $null; $taskCareBytes = $null
if ($taskClassic) {
    $taskCareBytes = Read-MiState 0x10 0x02
    if ($taskCareBytes.Length -gt 6 -and $taskCareBytes[1] -eq 0x80) {
        $taskLimit = switch ($taskCareBytes[6]) { 0 {100} 1 {80} 4 {80} 5 {70} 6 {60} 7 {50} 8 {40} default {$null} }
    }
}
$taskReading = [pscustomobject]@{Time=(Get-Date).ToString('o'); FirmwareMode=$taskModeName; ModeCode=('0x{0:X2}' -f $taskModeCode); ChargeLimitPercent=$taskLimit; ModeReply=[BitConverter]::ToString($taskModeBytes); ChargeReply=if($taskCareBytes){[BitConverter]::ToString($taskCareBytes)}else{'Unavailable on echo dialect'}; WindowsPlan=(powercfg /getactivescheme | Out-String).Trim(); Note='Windows plan and Energy saver are separate controls; they do not report this firmware mode.'}
$taskReading | Add-Member -NotePropertyName KeyboardBacklightReply -NotePropertyValue ([BitConverter]::ToString($taskBacklightBytes))
$taskReading | Add-Member -NotePropertyName LockPolicies -NotePropertyValue @{
    SignIn = (Get-ItemProperty -LiteralPath 'HKCU:\Control Panel\Desktop' -Name DelayLockInterval,ScreenSaveActive,ScreenSaverIsSecure -ErrorAction SilentlyContinue | Select-Object DelayLockInterval,ScreenSaveActive,ScreenSaverIsSecure)
    Machine = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name InactivityTimeoutSecs -ErrorAction SilentlyContinue | Select-Object InactivityTimeoutSecs)
    Resume = (powercfg /qh SCHEME_CURRENT SUB_NONE CONSOLELOCK | Out-String).Trim()
}
if ($OutputPath) { $taskReading | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding UTF8 }
$taskReading | Format-List

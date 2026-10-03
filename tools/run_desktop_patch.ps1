# Purpose: run the reviewed installed update outside inherited AppData redirection.
# Dependencies: administrator Windows PowerShell and interactive Task Scheduler.
# Outputs: temporary task lifecycle and the update's evidence files.
# Command: powershell -NoProfile -File tools\run_desktop_patch.ps1 -WorkRoot STAGE -Output RESULTS
param([string]$WorkRoot,[string]$Output,[string]$Plan='patch-plan.json')
$ErrorActionPreference='Stop'
$taskName='XiaomiRevamp-Polish-20261004'
$action=New-ScheduledTaskAction -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -WorkRoot "{1}" -Output "{2}" -PatchPlanPath "{3}"' -f (Join-Path $WorkRoot 'tools\apply_polish_patch.ps1'),$WorkRoot,$Output,$Plan)
$principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
try{
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal|Out-Null
    Start-ScheduledTask -TaskName $taskName
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(!(Test-Path (Join-Path $Output 'done.txt'))){
        if(Test-Path (Join-Path $Output 'error.txt')){throw (Get-Content (Join-Path $Output 'error.txt') -Raw)}
        if($watch.Elapsed.TotalSeconds -gt 600){throw 'Desktop update timeout'}
        Start-Sleep -Milliseconds 300
    }
}finally{Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue}

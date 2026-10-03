# Purpose: run both isolated installer formats under administrator rights.
# Dependencies: existing PowerShell/Framework compiler. Outputs: numbered logs and JSON summaries.
# Command: powershell -NoProfile -File checks\run_setup_checks.ps1 -Results RESULTS_PARENT
param([Parameter(Mandatory=$true)][string]$Results)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $Results -Force|Out-Null
$log=Join-Path $Results 'setup-checks.log'
Start-Transcript -LiteralPath $log|Out-Null
try{
    & (Join-Path $PSScriptRoot 'check_setup.ps1') -RunDirectory (Join-Path $Results '007_20261003T160000Z_seed0')
    & (Join-Path $PSScriptRoot 'check_setup.ps1') -RunDirectory (Join-Path $Results '008_20261003T160000Z_seed0') -ContentStore
    @{passed=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Results 'setup-checks-complete.json')
}catch{
    @{passed=$false;error=$_.Exception.ToString()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Results 'setup-checks-complete.json')
    throw
}finally{Stop-Transcript|Out-Null}

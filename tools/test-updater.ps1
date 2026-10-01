# Purpose: call the bundled XiControl release checker once without changing its automatic-update preference.
# Dependencies: PowerShell 7, installed manager DLL and access to GitHub's public releases API.
# Outputs: fresh numbered config.json and summary.json under results.
# Command: pwsh -NoProfile -File tools/test-updater.ps1
#requires -Version 7
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $projectRoot 'app\PCManager.dll'
$resultsRoot = Join-Path $projectRoot 'results'
$highest = 0
Get-ChildItem -LiteralPath $resultsRoot -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $highest = [Math]::Max($highest,[int]$Matches[1]) } }
$runRoot = Join-Path $resultsRoot ('{0:D3}_{1}_updater' -f ($highest+1),(Get-Date -Format 'yyyyMMddTHHmmssfff'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$hash = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
@{ time=(Get-Date).ToString('o'); input=$assemblyPath; assemblySha256=$hash; method='XiControl.SystemIntegration.UpdateCheck.FetchLatestAsync'; endpoint='https://api.github.com/repos/Oksion/XiControl/releases/latest'; networkRequests=1; automaticPreferenceChanged=$false; seed=$null } | ConvertTo-Json | Set-Content (Join-Path $runRoot 'config.json') -Encoding UTF8
try {
    $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
    $checker = $assembly.GetType('XiControl.SystemIntegration.UpdateCheck')
    $current = $checker.GetMethod('CurrentVersion').Invoke($null,@())
    $task = $checker.GetMethod('FetchLatestAsync').Invoke($null,@())
    $release = $task.GetAwaiter().GetResult()
    $summary = @{ time=(Get-Date).ToString('o'); currentComponentVersion=[string]$current; latestTag=$release.Tag; latestUrl=$release.Url; available=($null -ne $release -and $release.Version -gt $current); fetchSucceeded=($null -ne $release); limitations='Tests the vendored HTTP/parser/version path, not the manager notification or daily schedule.' }
} catch {
    $summary = @{ time=(Get-Date).ToString('o'); fetchSucceeded=$false; error=$_.Exception.Message; limitations='Network or reflection failure. Automatic update preference was not changed.' }
}
$summary | ConvertTo-Json | Set-Content (Join-Path $runRoot 'summary.json') -Encoding UTF8
Write-Output $runRoot
$summary | ConvertTo-Json
if (!$summary.fetchSucceeded) { exit 1 }

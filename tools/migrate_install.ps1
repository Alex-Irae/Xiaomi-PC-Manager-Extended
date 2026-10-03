# Purpose: uninstall the verified original apps, retain personal data and install the suite for live tests.
# Dependencies: administrator Windows PowerShell 5.1, existing uninstallers and sealed suite release 003.
# Outputs: Program Files suite installation, migrated settings/history/artwork and numbered evidence.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/migrate_install.ps1 -RunDirectory ABSOLUTE_PATH
param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$suiteSourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$runRoot = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\')
if (!$runRoot.StartsWith($suiteSourceRoot + '\results\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay in suite/results.' }
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required for the verified Program Files operation.' }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$config = Get-Content -LiteralPath (Join-Path $runRoot 'config.json') -Raw | ConvertFrom-Json
$oldPC = [IO.Path]::GetFullPath($config.previousPC).TrimEnd('\')
$oldSearch = [IO.Path]::GetFullPath($config.previousSearch).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($config.newSuite).TrimEnd('\')
if ($oldPC -ne 'C:\Program Files\Xiaomi Revamp\PC Manager' -or $oldSearch -ne 'C:\Program Files\Xiaomi Revamp\AI Center' -or $destination -ne 'C:\Program Files\Xiaomi Revamp Suite') { throw 'Unexpected installation targets.' }
foreach ($target in @($oldPC,$oldSearch,$destination)) {
    for ($ancestor = $target; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Redirected installation path: ' + $ancestor) }
    }
}
$receipt = Get-Content -LiteralPath (Join-Path $oldPC 'install-receipt.json') -Raw | ConvertFrom-Json
$searchManifest = Get-Content -LiteralPath (Join-Path $oldSearch 'package-manifest.json') -Raw | ConvertFrom-Json
if ($receipt.product -ne 'XiaomiAIManager' -or $receipt.ownerSid -ne $sid -or $receipt.executable -ne (Join-Path $oldPC 'PCManager.exe') -or $searchManifest.appId -ne 'LocalAICenter') { throw 'Original installation identity mismatch.' }
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) { throw 'Suite target already contains files; no installation was changed.' }
$localData = [Environment]::GetFolderPath('LocalApplicationData')
$originalPCData = Join-Path $localData 'XiaomiAIManager'
$originalSearchData = Join-Path $localData 'LocalAICenter'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Start-Transcript -LiteralPath (Join-Path $runRoot 'migration.log') | Out-Null
try {
    Write-Output 'STEP 1: clean PC Manager shutdown and verified uninstall, retaining personal/API data.'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'uninstall_pc_preserve.ps1') -Target $oldPC -ExpectedSid $sid -NoPrompt
    if ($LASTEXITCODE -ne 0) { throw 'PC Manager uninstall failed; suite installation stopped.' }
    Write-Output 'STEP 2: clean AI Center shutdown and verified uninstall, retaining its private profile.'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $oldSearch 'Uninstall.ps1') -Target $oldSearch -NoProfiles
    if ($LASTEXITCODE -ne 0) { throw 'AI Center uninstall failed; suite installation stopped.' }
    if ((Test-Path -LiteralPath $oldPC) -or (Test-Path -LiteralPath $oldSearch)) { throw 'An original app installation still exists.' }
    Write-Output 'STEP 3: install all three suite components and register only PC Manager at sign-in.'
    $setup = Join-Path $suiteSourceRoot 'packages\003_20261002T231115Z\Xiaomi-Revamp-Setup.exe'
    $setupProcess = Start-Process -FilePath $setup -ArgumentList ('--apply --owner ' + $sid + ' --target "' + $destination + '" --components pc-manager,file-search,screen-translator --startup pc-manager --desktop') -WindowStyle Hidden -PassThru -Wait
    if ($setupProcess.ExitCode -ne 0) { throw ('Suite setup failed with exit ' + $setupProcess.ExitCode) }
    $marker = Get-Content -LiteralPath (Join-Path $destination 'suite-install.json') -Raw | ConvertFrom-Json
    $newData = Join-Path $localData ('XiaomiRevampSuite\' + $marker.profileId)
    Write-Output 'STEP 4: copy personal settings/history/artwork into the independent suite profile.'
    $pcData = Join-Path $newData 'pc-manager'; New-Item -ItemType Directory -Path $pcData -Force | Out-Null
    $settings = Get-Content -LiteralPath (Join-Path $originalPCData 'settings.json') -Raw | ConvertFrom-Json
    foreach ($link in $settings.QuickLinks) {
        if ($link.Path -and $link.Path.StartsWith($oldSearch + '\', [StringComparison]::OrdinalIgnoreCase)) { $link.Path = Join-Path $destination 'AI Center\AI Center.exe'; $link | Add-Member -NotePropertyName Arguments -NotePropertyValue '--search' -Force }
    }
    [IO.File]::WriteAllText((Join-Path $pcData 'settings.json'), ($settings | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
    foreach ($name in @('icons','assets','service-state')) {
        $source = Join-Path $originalPCData $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $pcData -Recurse }
    }
    $searchData = Join-Path $newData 'file-search'; New-Item -ItemType Directory -Path (Join-Path $searchData 'data') -Force | Out-Null
    $searchConfig = Get-Content -LiteralPath (Join-Path $originalSearchData 'config.json') -Raw | ConvertFrom-Json
    $searchConfig.model_path = Join-Path $destination 'AI Center\models\qwen3-embedding'
    $searchConfig.run_at_startup = $false
    $searchConfig.excluded_folders = @(@($searchConfig.excluded_folders) + @($destination,$newData) | Select-Object -Unique)
    [IO.File]::WriteAllText((Join-Path $searchData 'config.json'), ($searchConfig | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
    $retained = @()
    foreach ($name in @('history.dpapi','profile.png')) {
        $source = Join-Path $originalSearchData ('data\' + $name)
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $searchData ('data\' + $name))
            $retained += @{file=$name;sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash}
        }
    }
    $status = @{passed=$true;originalApplicationsRemoved=$true;translatorOriginalInstalled=$false;originalPersonalDataRetained=$true;suiteRoot=$destination;profile=$newData;historyAndArtwork=$retained;startupComponents=@('pc-manager');installerExit=$setupProcess.ExitCode}
    $status | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'migration-summary.json') -Encoding UTF8
    Write-Output 'PASS: original apps stopped/removed, suite installed, personal settings copied, rebuildable indexes kept separate.'
    Start-Process -FilePath (Join-Path $destination 'PC Manager\PCManager.exe') -ArgumentList '--manager' -WindowStyle Hidden | Out-Null
} catch {
    @{passed=$false;error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'migration-summary.json') -Encoding UTF8
    Write-Error $_
    exit 1
} finally { Stop-Transcript | Out-Null }

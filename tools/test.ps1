# Purpose: build or launch the project-local test app, with separate settings and numbered OEM OSDs.
# Dependencies: Windows x64, WebView2; Prepare downloads a verified Microsoft .NET 8 SDK ZIP.
# Outputs: .test-environment/{dotnet,packages,artifacts,logs}; app/test-data stores test settings.
# Commands: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test.ps1 -Prepare -Build
# Launch: powershell -NoProfile -ExecutionPolicy Bypass -File tools/test.ps1 -View popup
param(
    [switch]$Prepare,
    [switch]$Build,
    [ValidateSet('none','popup','manager','small','medium','large')][string]$View = 'none'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot '.test-environment'
$sdkPath = Join-Path $testRoot 'dotnet\dotnet.exe'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
if ($Prepare -and !(Test-Path -LiteralPath $sdkPath)) {
    Write-Host 'Reading official .NET 8 SDK metadata...'
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
    $release = $metadata.releases | Where-Object { $_.sdk.version -eq $metadata.'latest-sdk' } | Select-Object -First 1
    $archive = $release.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
    if (!$archive -or !([Uri]$archive.url).Host.Equals('builds.dotnet.microsoft.com')) { throw 'No trusted Microsoft SDK ZIP was found.' }
    $downloads = Join-Path $testRoot 'downloads'
    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    $archive | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $downloads ('sdk-' + $release.sdk.version + '-source.json')) -Encoding UTF8
    $zipPath = Join-Path $downloads ('dotnet-sdk-' + $release.sdk.version + '-win-x64.zip')
    Write-Host ('Downloading SDK ' + $release.sdk.version + ' into this project...')
    Invoke-WebRequest -Uri $archive.url -OutFile $zipPath -UseBasicParsing
    Write-Host 'Verifying SHA-512...'
    if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA512).Hash -ne $archive.hash) { throw 'SDK checksum mismatch. Archive preserved; extraction stopped.' }
    Write-Host 'Extracting the verified SDK...'
    Expand-Archive -LiteralPath $zipPath -DestinationPath (Join-Path $testRoot 'dotnet') -Force
}
$appDirectory = Join-Path $testRoot 'artifacts\bin\XiaomiAIManager\release'
$appPath = Join-Path $appDirectory 'XiaomiAIManager.exe'
if (!$Build -and (Test-Path -LiteralPath (Join-Path $projectRoot 'app\XiaomiAIManager.exe'))) {
    $appDirectory = Join-Path $projectRoot 'app'
    $appPath = Join-Path $appDirectory 'XiaomiAIManager.exe'
}
if ($Build) {
    if (!(Test-Path -LiteralPath $sdkPath)) { throw 'Prepare the local SDK first: tools/test.ps1 -Prepare -Build' }
    # These environment variables apply only to this process and its children.
    $env:DOTNET_CLI_HOME = Join-Path $testRoot 'cli-home'
    $env:NUGET_PACKAGES = Join-Path $testRoot 'packages'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    New-Item -ItemType Directory -Force -Path (Join-Path $testRoot 'logs') | Out-Null
    $buildLog = Join-Path $testRoot ('logs\build-' + (Get-Date -Format 'yyyyMMddTHHmmssfff') + '.txt')
    Write-Host 'Restoring project-local packages and building the test app...'
    & $sdkPath build (Join-Path $projectRoot 'XiaomiAIManager.csproj') -c Release --artifacts-path (Join-Path $testRoot 'artifacts') --verbosity minimal 2>&1 | Tee-Object -FilePath $buildLog
    if ($LASTEXITCODE -ne 0) { throw ('Build failed. See ' + $buildLog) }
    Write-Host ('Build ready: ' + $appPath)
}
if ($View -ne 'none') {
    if (!(Test-Path -LiteralPath $appPath)) { throw 'Build first using Rebuild test.cmd.' }
    $command = switch ($View) { 'popup' { '--toggle' } 'manager' { '--manager' } default { '--monitor-' + $View } }
    $env:DOTNET_ROOT = Join-Path $testRoot 'dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    Write-Host ('Opening the test app (' + $View + '). Mouse controls change real hardware; settings are separate.')
    Start-Process -FilePath $appPath -ArgumentList @('--test', $command) -WorkingDirectory $appDirectory -WindowStyle Hidden
}

# Purpose: publish a portable Windows build and wrap it in a double-click setup EXE.
# Dependencies: existing .NET 8 SDK and restored project packages, Windows .NET Framework C# compiler, PowerShell 5.1.
# Outputs: a new numbered releases folder containing a portable ZIP, setup EXE, manifest and source instructions.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-release.ps1
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $projectRoot '.test-environment\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.test-environment\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.test-environment\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$releaseRoot = Join-Path $projectRoot 'releases'
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
$highest = 0
Get-ChildItem -LiteralPath $releaseRoot -Directory | ForEach-Object { if ($_.Name -match '^(\d+)_') { $highest = [Math]::Max($highest, [int]$Matches[1]) } }
$run = Join-Path $releaseRoot ('{0:D3}_{1}' -f ($highest + 1), (Get-Date -Format 'yyyyMMddTHHmmssfff'))
$publish = Join-Path $run 'portable'
$wrap = Join-Path $run 'setup-sources'
New-Item -ItemType Directory -Path $publish, $wrap | Out-Null
Push-Location $projectRoot
try {
    & $sdk publish XiaomiAIManager.csproj -c Release --artifacts-path .test-environment/artifacts --no-restore --self-contained false -p:UseAppHost=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'The Release publish failed.' }
} finally { Pop-Location }
$exe = Join-Path $publish 'PCManager.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'The published executable is missing.' }
$packageFiles = Join-Path $run 'package-files'
New-Item -ItemType Directory -Path $packageFiles | Out-Null
Get-ChildItem -LiteralPath $publish | Where-Object { $_.Name -notlike '*.pdb' -and $_.Name -notlike '*.xml' } |
    Copy-Item -Destination $packageFiles -Recurse
$zip = Join-Path $run 'PCManager-portable.zip'
Compress-Archive -Path (Join-Path $packageFiles '*') -DestinationPath $zip -CompressionLevel Optimal
$sourceZip = Join-Path $run 'PCManager-source.zip'
$sourcePaths = @('Program.cs','MainWindow.cs','ManagerApplication.cs','DesktopShortcuts.cs','XiaomiAIManager.csproj',
    'app.manifest','README.md','LICENSE','Services','Vendor','www','assets','installer','packaging','tools','docs') |
    ForEach-Object { Join-Path $projectRoot $_ }
$sourcePaths += @(Get-ChildItem -LiteralPath $projectRoot -Filter '*.cmd' -File | Select-Object -ExpandProperty FullName)
Compress-Archive -LiteralPath $sourcePaths -DestinationPath $sourceZip -CompressionLevel Optimal
Copy-Item -LiteralPath $zip -Destination (Join-Path $wrap 'payload.zip')
Copy-Item -LiteralPath (Join-Path $projectRoot 'installer\Install.ps1') -Destination (Join-Path $wrap 'Install.ps1')
$setup = Join-Path $run 'PCManager-Setup.exe'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler is unavailable; portable ZIP is still ready.' }
& $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$setup" "/win32icon:$(Join-Path $projectRoot 'assets\app.ico')" "/resource:$(Join-Path $wrap 'payload.zip'),payload.zip" "/resource:$(Join-Path $wrap 'Install.ps1'),Install.ps1" (Join-Path $projectRoot 'installer\SetupBootstrap.cs')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $setup)) { throw 'Windows C# compiler did not produce the setup executable.' }
[pscustomobject]@{createdAt=(Get-Date).ToString('o'); frameworkDependent=$true; targetFramework='net8.0-windows';
    requiredRuntime='.NET 8 Desktop Runtime and WebView2'; portable=[IO.Path]::GetFileName($zip); setup=[IO.Path]::GetFileName($setup); source=[IO.Path]::GetFileName($sourceZip);
    portableSha256=(Get-FileHash -LiteralPath $zip).Hash; setupSha256=(Get-FileHash -LiteralPath $setup).Hash;
    sourceSha256=(Get-FileHash -LiteralPath $sourceZip).Hash;
    assemblySha256=(Get-FileHash -LiteralPath (Join-Path $publish 'PCManager.dll')).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $run 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $run 'LICENSE')
Write-Host ('Release ready: ' + $run)



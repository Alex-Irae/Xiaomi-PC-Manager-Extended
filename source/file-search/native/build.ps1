# Purpose: build the native shell with the existing SDK and WebView2 assemblies.
# Dependencies: .NET 8 SDK + Windows reference packs; no packages are installed.
# Outputs: native/bin/Release/net8.0-windows/AI Center.exe and local build metadata.
# Command from project root: pwsh -NoProfile -File native/build.ps1
param([string]$Sdk = 'C:\Utils\XiaomiPc Manager\development\XiaomiAIManager\.test-environment\dotnet\dotnet.exe', [string]$WebViewReferenceDir = 'C:\Utils\XiaomiPc Manager\development\XiaomiAIManager\app')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-icon.ps1')
if (-not (Test-Path -LiteralPath $Sdk)) { throw 'An existing .NET 8 SDK is required. No installation is performed.' }
if (-not (Test-Path -LiteralPath (Join-Path $WebViewReferenceDir 'Microsoft.Web.WebView2.Core.dll'))) { throw 'Supply existing WebView2 assemblies through -WebViewReferenceDir.' }
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.build-cache'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
& $Sdk build (Join-Path $PSScriptRoot 'AICenter.csproj') --configuration Release --ignore-failed-sources -p:NuGetAudit=false -p:RestoreSources= -p:WebViewReferenceDir=$WebViewReferenceDir
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }

# Purpose: build/run the C# WebView2 development UI with a Python inference backend.
# Dependencies: existing .NET 8+ SDK, WebView2 SDK/runtime, root requirements.txt.
# Outputs: native/bin/Debug DLL (no app EXE), data/ settings/logs/WebView profile.
# Command: pwsh -NoProfile -File ./dev.ps1 -Sdk C:\path\dotnet.exe -WebViewReferenceDir C:\path\WebView2 -Python python
param(
    [string]$Sdk = 'dotnet',
    [string]$WebViewReferenceDir = $env:WEBVIEW2_REFERENCE_DIR,
    [string]$Python = 'python',
    [switch]$NoLoad,
    [switch]$CheckUi
)
$ErrorActionPreference = 'Stop'
if (-not $WebViewReferenceDir) { throw 'Set WEBVIEW2_REFERENCE_DIR or pass -WebViewReferenceDir with existing WebView2 assemblies.' }
foreach ($name in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $WebViewReferenceDir $name))) { throw "Missing local WebView2 SDK assembly: $name" }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'native/.cli'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
& $Sdk build (Join-Path $PSScriptRoot 'native/ScreenTranslator.csproj') --configuration Debug -p:WebViewReferenceDir=$WebViewReferenceDir -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Development compilation failed. Supply an existing SDK with Windows Desktop reference packs.' }
$arguments = @((Join-Path $PSScriptRoot 'native/bin/Debug/net8.0-windows/ScreenTranslator.dll'), '--root', $PSScriptRoot, '--python', $Python)
if ($NoLoad) { $arguments += '--no-load' }
if ($CheckUi) { $arguments += '--check-ui' }
& $Sdk @arguments
exit $LASTEXITCODE

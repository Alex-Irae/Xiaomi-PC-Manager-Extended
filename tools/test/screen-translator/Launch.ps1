# Purpose: build Screen Translator from this workspace's source and run it as an isolated test copy (ScreenTranslator-test.exe).
# Dependencies: the offline .NET SDK in ..\toolchain beside the workspace, and the installed Screen Translator's
#               private Python runtime and model bundle (read only). Nothing is installed or downloaded.
# Outputs: build\test\Screen Translator\ (output: native build; _data: test settings, logs, model cache).
#          The installed app is not changed.
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools\test\screen-translator\Launch.ps1 [-BuildOnly] [-Tray] [-Check lifecycle|ui]
#          Without -BuildOnly the installed Screen Translator is disabled first and enabled again when the test copy exits.
#          Never run it while tools\build_suite.py is running: disabling the installed app ends the Python the build uses.
param([switch]$BuildOnly,[switch]$Tray,[ValidateSet('','lifecycle','ui')][string]$Check='',[string]$Installed='C:\Program Files\Xiaomi Revamp\Screen Translator')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$toolchain=Join-Path (Split-Path $workspace -Parent) 'toolchain'
$state=Join-Path $workspace 'build\test\Screen Translator'
$sdk=Join-Path $toolchain 'sdk\dotnet.exe'
if(!(Test-Path -LiteralPath $sdk)){throw 'The offline SDK was not found in the toolchain folder beside the workspace.'}
New-Item -ItemType Directory -Force -Path $state | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $state '.build-cache'
$env:NUGET_PACKAGES=Join-Path $toolchain 'packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='0'
$source=Join-Path $workspace 'source\screen-translator'
$output=Join-Path $state 'output'
& $sdk build (Join-Path $source 'native\ScreenTranslator.csproj') -c Release --configfile (Join-Path $workspace 'source\NuGet.Config') -p:NuGetAudit=false -p:RestoreSources= -p:UseAppHost=true "-p:WebViewReferenceDir=$(Join-Path $toolchain 'webview')" -o $output
if($LASTEXITCODE -ne 0){throw 'Build failed. Nothing was started.'}
# The same apphost under a second name gives Task Manager and the tray a clear test identity.
Copy-Item -LiteralPath (Join-Path $output 'ScreenTranslator.exe') -Destination (Join-Path $output 'ScreenTranslator-test.exe') -Force
Write-Host "PASS: built $output\ScreenTranslator-test.exe"
if($BuildOnly){exit 0}
$installedExe=Join-Path $Installed 'ScreenTranslator.exe'
$env:DOTNET_ROOT=Join-Path $Installed 'runtime\dotnet'
$arguments=@('--root',"`"$source`"",'--python',"`"$(Join-Path $Installed 'runtime\python\python.exe')`"",'--data-dir',"`"$(Join-Path $state '_data')`"",'--no-startup')
if($Tray){$arguments+='--tray'}
if($Check -eq 'lifecycle'){$arguments+='--check-model-lifecycle'}elseif($Check -eq 'ui'){$arguments+='--check-ui'}
& $installedExe --disable
Start-Sleep -Seconds 3
try{
    $process=Start-Process -FilePath (Join-Path $output 'ScreenTranslator-test.exe') -ArgumentList $arguments -WorkingDirectory $source -PassThru
    Write-Host "Started ScreenTranslator-test.exe (pid $($process.Id)); the installed Screen Translator stays disabled until it exits."
    $process.WaitForExit()
    Write-Host "Test copy exited with code $($process.ExitCode)."
}finally{
    & $installedExe --enable
    Write-Host 'Installed Screen Translator enabled again (not started).'
}

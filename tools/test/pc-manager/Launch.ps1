# Purpose: build PC Manager from this workspace's source and run it as an isolated test copy (PCManager-test.exe).
# Dependencies: the offline .NET SDK in ..\toolchain beside the workspace, and the installed PC Manager, whose
#               private runtime is copied once (read only). Nothing is installed or downloaded.
# Outputs: build\test\PC Manager\ (output, App, _data: the test copy's own settings). The installed app is not changed.
# Launching disables the installed PC Manager; return to it with: & "C:\Program Files\Xiaomi Revamp\PC Manager\PCManager.exe" --enable
# Command: powershell -NoProfile -ExecutionPolicy Bypass -File tools\test\pc-manager\Launch.ps1 [-BuildOnly] [-Tray]
param([switch]$BuildOnly,[switch]$Tray,[string]$Installed='C:\Program Files\Xiaomi Revamp\PC Manager')
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$toolchain=Join-Path (Split-Path $workspace -Parent) 'toolchain'
$state=Join-Path $workspace 'build\test\PC Manager'
$executable=Join-Path $Installed 'PCManager.exe'
if(!(Test-Path -LiteralPath $executable)){throw 'Pass -Installed with your installed PC Manager folder.'}
$sdk=Join-Path $toolchain 'sdk\dotnet.exe'
if(!(Test-Path -LiteralPath $sdk)){throw 'The offline SDK was not found in the toolchain folder beside the workspace.'}
$env:DOTNET_CLI_HOME=Join-Path $toolchain 'cli'
$env:NUGET_PACKAGES=Join-Path $toolchain 'packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$output=Join-Path $state 'output'
Write-Host 'Building PC Manager from source\pc-manager...'
& $sdk build (Join-Path $workspace 'source\pc-manager\XiaomiAIManager.csproj') -c Release --configfile (Join-Path $workspace 'source\NuGet.Config') -p:NuGetAudit=false -p:RestoreSources= "-p:WebViewReferenceDir=$(Join-Path $toolchain 'webview')" -o $output
if($LASTEXITCODE -ne 0){throw 'Build failed. Nothing was started.'}
$runRoot=Join-Path $state 'App'
New-Item -ItemType Directory -Path $runRoot -Force|Out-Null
function CopyTree($from,$to){
    & robocopy.exe $from $to /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
    if($LASTEXITCODE -gt 7){throw "Could not copy offline files from $from"}
}
if(!(Test-Path -LiteralPath (Join-Path $runRoot '.dependencies-ready'))){
    Write-Host 'Copying the private runtime for the first test launch...'
    CopyTree (Join-Path $Installed 'runtime') (Join-Path $runRoot 'runtime')
    foreach($name in @('suite-component.json','PCManager.exe')){
        if(Test-Path -LiteralPath (Join-Path $Installed $name)){Copy-Item -LiteralPath (Join-Path $Installed $name) -Destination (Join-Path $runRoot $name)}
    }
    [IO.File]::WriteAllText((Join-Path $runRoot '.dependencies-ready'),'Offline dependencies copied from the installed app.')
}
CopyTree $output (Join-Path $runRoot 'runtime\dotnet')
# The apphost retains PCManager.dll but gives Task Manager a clear test process name.
$testExecutable=Join-Path $runRoot 'runtime\dotnet\PCManager-test.exe'
Copy-Item -LiteralPath (Join-Path $output 'PCManager.exe') -Destination $testExecutable -Force
# This marker gives the test copy its own profile in _data beside it, apart from the installed app's settings.
$marker=Join-Path $state 'suite-install.json'
if(!(Test-Path -LiteralPath $marker)){[IO.File]::WriteAllText($marker,'{"schema":1,"profileId":"pc-manager-dev","portable":true}')}
Write-Host ('PASS: test build ready in '+$runRoot)
if($BuildOnly){exit 0}
# The installed PC Manager restarts itself every minute. Its control switch asks the running
# instance to close and holds it down until "PCManager.exe --enable" is run.
Write-Host 'Disabling the installed PC Manager before starting PCManager-test.exe...'
& $executable --disable | Out-Host
$deadline=(Get-Date).AddSeconds(20)
while((Get-Process -Name 'PCManager' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 250}
if(Get-Process -Name 'PCManager' -ErrorAction SilentlyContinue){throw 'The installed PC Manager did not exit; the test copy was not started.'}
$env:DOTNET_ROOT=Join-Path $runRoot 'runtime\dotnet'
$launch=@{FilePath=$testExecutable;WorkingDirectory=(Join-Path $runRoot 'runtime\dotnet');WindowStyle='Hidden';ArgumentList=('--test '+$(if($Tray){'--tray'}else{'--manager'}));Verb='RunAs'}
try { Start-Process @launch }
catch {
    & $executable --enable | Out-Host
    Start-Process -FilePath $executable -ArgumentList '--tray' -WindowStyle Hidden
    throw
}
Start-Sleep -Seconds 2
if(!(Get-Process -Name 'PCManager-test' -ErrorAction SilentlyContinue)){
    & $executable --enable | Out-Host
    Start-Process -FilePath $executable -ArgumentList '--tray' -WindowStyle Hidden
    throw 'PCManager-test.exe exited during startup. The installed resident was restarted.'
}
Write-Host 'PCManager-test.exe is running. The installed PC Manager stays disabled until you run its --enable switch.'

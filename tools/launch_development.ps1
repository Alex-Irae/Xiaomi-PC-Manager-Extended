# Purpose: rebuild editable source and launch one app with its installed offline dependencies.
# Dependencies: copied .NET 8 SDK/cache in Development/toolchain, installed private runtimes/models.
# Outputs: Development/output and Development/App; the app uses its existing installed user profile.
# Command: .\Development\Launch.cmd, or powershell -NoProfile -File Development\Launch.ps1 -BuildOnly
param([switch]$BuildOnly,[switch]$Tray)
$ErrorActionPreference='Stop'
$devRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$appRoot=[IO.Path]::GetFullPath((Join-Path $devRoot '..'))
$revampRoot=[IO.Path]::GetFullPath((Join-Path $appRoot '..'))
$info=Get-Content -LiteralPath (Join-Path $devRoot 'development.json') -Raw|ConvertFrom-Json
$executable=Join-Path $appRoot $info.executable
if(!$BuildOnly){
    Write-Host 'Stopping the installed or previous development copy...'
    & $executable --quit
    Start-Sleep -Seconds 1
}
$env:DOTNET_CLI_HOME=Join-Path $devRoot 'toolchain\cli'
$env:NUGET_PACKAGES=Join-Path $devRoot 'toolchain\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:PYTHONDONTWRITEBYTECODE='1'
$sdk=Join-Path $devRoot 'toolchain\sdk\dotnet.exe'
if(!(Test-Path -LiteralPath $sdk)){throw 'Extract development-toolchain.zip into Development\toolchain before launching.'}
$webview=Join-Path $devRoot 'toolchain\webview'
$output=Join-Path $devRoot 'output'
Write-Host ('Building '+$info.name+' from Development\source...')
& $sdk build (Join-Path $devRoot $info.project) -c Release --configfile (Join-Path $devRoot 'source\NuGet.Config') -p:NuGetAudit=false -p:RestoreSources= "-p:WebViewReferenceDir=$webview" -o $output
if($LASTEXITCODE -ne 0){throw 'Development build failed. The installed files were not replaced.'}
$runRoot=Join-Path $devRoot 'App'
New-Item -ItemType Directory -Path $runRoot -Force|Out-Null
function CopyTree($from,$to){
    & robocopy.exe $from $to /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
    if($LASTEXITCODE -gt 7){throw "Could not copy offline files from $from"}
}
if(!(Test-Path -LiteralPath (Join-Path $runRoot '.dependencies-ready'))){
    Write-Host 'Copying private runtimes and models for the first development launch...'
    CopyTree (Join-Path $appRoot 'runtime') (Join-Path $runRoot 'runtime')
    if(Test-Path -LiteralPath (Join-Path $appRoot 'models')){CopyTree (Join-Path $appRoot 'models') (Join-Path $runRoot 'models')}
    foreach($name in @('config.example.json','packaged.json','package-manifest.json','suite-component.json',$info.executable)){
        if(Test-Path -LiteralPath (Join-Path $appRoot $name)){Copy-Item -LiteralPath (Join-Path $appRoot $name) -Destination (Join-Path $runRoot $name)}
    }
    [IO.File]::WriteAllText((Join-Path $runRoot '.dependencies-ready'),'Offline dependencies copied from the installed app.')
}
$source=Join-Path $devRoot ('source\'+$info.component)
if($info.component -eq 'pc-manager'){
    CopyTree $output (Join-Path $runRoot 'runtime\dotnet')
}elseif($info.component -eq 'file-search'){
    Get-ChildItem -LiteralPath $output -File|Copy-Item -Destination (Join-Path $runRoot 'runtime\dotnet') -Force
    foreach($name in @('frontend','xiaomi_search')){CopyTree (Join-Path $source $name) (Join-Path $runRoot $name)}
}else{
    CopyTree $output $runRoot
    foreach($name in @('frontend','screen_translator')){CopyTree (Join-Path $source $name) (Join-Path $runRoot $name)}
}
Write-Host ('PASS: editable build ready in '+$runRoot)
if($BuildOnly){exit 0}
if(!(Test-Path -LiteralPath (Join-Path $revampRoot 'suite-install.json'))){
    [IO.File]::WriteAllText((Join-Path $devRoot 'suite-install.json'),'{"schema":1,"profileId":"development","portable":true}')
    Write-Host 'The installed profile was removed. Development will use its own _data folder.'
}
$arguments=if($Tray){'--tray'}elseif($info.component -eq 'pc-manager'){'--manager'}elseif($info.component -eq 'file-search'){'--search'}else{''}
$launch=@{FilePath=(Join-Path $runRoot $info.executable);WorkingDirectory=$runRoot;WindowStyle='Hidden'}
if($arguments){$launch.ArgumentList=$arguments}
if($info.component -eq 'pc-manager'){$launch.Verb='RunAs'}
Start-Process @launch

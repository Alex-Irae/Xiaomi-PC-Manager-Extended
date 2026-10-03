# Purpose: build a new offline installer EXE and installer ZIP, using installed runtimes.
# Dependencies: existing .NET 8 SDK, Windows .NET Framework csc, Python 3.12,
# existing project packages/models and .NET Desktop runtime. No installs/downloads.
# Outputs: new releases/NNN_timestamp/ payload, EXE, ZIP, hashes and manifest.
# Command from project root: pwsh -NoProfile -File packaging/build-release.ps1
param([string]$PythonRoot='C:\Program Files\Python312',[string]$DotnetRoot='C:\Program Files\dotnet')
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$releaseRoot=Join-Path $project 'releases'
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
$highest=0
Get-ChildItem -LiteralPath $releaseRoot -Directory | ForEach-Object {if($_.Name -match '^(\d+)_'){$highest=[Math]::Max($highest,[int]$Matches[1])}}
$run=Join-Path $releaseRoot ('{0:D3}_{1}' -f ($highest+1),(Get-Date -Format 'yyyyMMddTHHmmssfff'))
$stage=Join-Path $run 'app'
New-Item -ItemType Directory -Path $stage | Out-Null
& (Join-Path $project 'native\build.ps1')
if($LASTEXITCODE -ne 0){throw 'Native build failed.'}
$native=Join-Path $project 'native\bin\Release\net8.0-windows'
Get-ChildItem -LiteralPath $native -File | Where-Object {$_.Extension -in '.dll','.json'} | Copy-Item -Destination $stage
foreach($name in 'frontend','xiaomi_search'){Copy-Item -LiteralPath (Join-Path $project $name) -Destination $stage -Recurse}
# Remove nothing: exclude generated caches when making the actual archive below.
Copy-Item -LiteralPath (Join-Path $project 'config.example.json'),(Join-Path $project 'README.md'),(Join-Path $project 'requirements.txt') -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $stage
$py=Join-Path $stage 'runtime\python'
New-Item -ItemType Directory -Force -Path $py | Out-Null
foreach($name in 'python.exe','pythonw.exe','python3.dll','python312.dll','LICENSE.txt'){Copy-Item -LiteralPath (Join-Path $PythonRoot $name) -Destination $py}
Copy-Item -LiteralPath (Join-Path $PythonRoot 'DLLs') -Destination $py -Recurse
$lib=Join-Path $py 'Lib';New-Item -ItemType Directory -Path $lib | Out-Null
Get-ChildItem -LiteralPath (Join-Path $PythonRoot 'Lib') | Where-Object {$_.Name -notin 'site-packages','__pycache__','test','idlelib','ensurepip','tkinter','turtledemo'} | Copy-Item -Destination $lib -Recurse
$packages=Join-Path $lib 'site-packages';New-Item -ItemType Directory -Path $packages | Out-Null
$modules=@('numpy','numpy.libs','openvino','openvino_genai','openvino_tokenizers','openvino_telemetry','watchdog','pypdf','docx','pptx','openpyxl','et_xmlfile','striprtf','lxml','PIL','six.py','typing_extensions.py')
$metadata=@('numpy','openvino','openvino_genai','openvino_tokenizers','openvino_telemetry','watchdog','pypdf','python_docx','python_pptx','openpyxl','et_xmlfile','striprtf','lxml','pillow','six','typing_extensions')
$installed=Join-Path $project '.venv\Lib\site-packages'
foreach($name in $modules){Copy-Item -LiteralPath (Join-Path $installed $name) -Destination $packages -Recurse}
Get-ChildItem -LiteralPath $installed -Directory | Where-Object {
    $name=$_.Name;@($metadata | Where-Object {$name -like ($_+'-*.dist-info')}).Count -gt 0
} | Copy-Item -Destination $packages -Recurse
@('.', 'Lib', 'DLLs', 'Lib\site-packages', '..\..', 'import site') | Set-Content -LiteralPath (Join-Path $py 'python312._pth') -Encoding ASCII
foreach($name in 'msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','vcruntime140.dll','vcruntime140_1.dll','concrt140.dll'){
    $source=Join-Path $env:SystemRoot ('System32\'+$name);if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $py}
}
$dotnet=Join-Path $stage 'runtime\dotnet';New-Item -ItemType Directory -Force -Path $dotnet | Out-Null
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe') -Destination $dotnet
$framework=(Get-ChildItem -LiteralPath (Join-Path $DotnetRoot 'shared\Microsoft.NETCore.App') -Directory | Where-Object Name -like '8.*' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1).Name
if(!$framework -or !(Test-Path -LiteralPath (Join-Path $DotnetRoot ('shared\Microsoft.WindowsDesktop.App\'+$framework)))){throw 'Existing matching .NET 8 core/desktop runtime is required.'}
foreach($relative in @(('host\fxr\'+$framework),('shared\Microsoft.NETCore.App\'+$framework),('shared\Microsoft.WindowsDesktop.App\'+$framework))){
    $destination=Join-Path $dotnet (Split-Path -Parent $relative);New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $DotnetRoot $relative) -Destination $destination -Recurse
}
foreach($name in 'LICENSE.txt','ThirdPartyNotices.txt'){if(Test-Path -LiteralPath (Join-Path $DotnetRoot $name)){Copy-Item -LiteralPath (Join-Path $DotnetRoot $name) -Destination $dotnet}}
# Run a branded apphost so Task Manager identifies AI Center rather than dotnet.
Get-ChildItem -LiteralPath $native -File | Where-Object {$_.Extension -in '.exe','.dll','.json'} | Copy-Item -Destination $dotnet
Copy-Item -LiteralPath (Join-Path $project 'native\assets\app.ico') -Destination (Join-Path $stage 'app.ico')
$model=Join-Path $stage 'models\qwen3-embedding';New-Item -ItemType Directory -Force -Path (Split-Path -Parent $model) | Out-Null
Copy-Item -LiteralPath 'C:\ProgramData\MI\AIModel\AIModelSearch\3.1.7\qwen3_embedding_int8_sym' -Destination $model -Recurse
$bge=Join-Path $project 'data\models\bge-small-en-v1.5-int8-ov'
if(Test-Path -LiteralPath $bge){Copy-Item -LiteralPath $bge -Destination (Join-Path $stage 'models\bge-small-en-v1.5-int8-ov') -Recurse}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'notices') -Destination (Join-Path $stage 'notices') -Recurse
$csc=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ("/win32icon:"+(Join-Path $stage 'app.ico')) ("/out:"+(Join-Path $stage 'AI Center.exe')) (Join-Path $PSScriptRoot 'Launcher.cs')
if($LASTEXITCODE -ne 0){throw 'Launcher compilation failed.'}
& $csc /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll ("/win32icon:"+(Join-Path $stage 'app.ico')) ("/out:"+(Join-Path $stage 'Uninstall AI Center.exe')) (Join-Path $PSScriptRoot 'Uninstall.cs')
if($LASTEXITCODE -ne 0){throw 'Uninstaller compilation failed.'}
$files=@(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {$_.FullName -notmatch '[\\/]__pycache__[\\/]' -and $_.Extension -ne '.pyc'})
$entries=@($files | ForEach-Object {[ordered]@{path=$_.FullName.Substring($stage.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
[ordered]@{appId='LocalAICenter';version='0.1.7';architecture='x64';createdAt=(Get-Date).ToString('o');python='3.12.10';dotnet=$framework;webview2='Existing Microsoft Edge WebView2 runtime required';files=$entries} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload=Join-Path $run 'payload.zip'
$archive=[IO.Compression.ZipFile]::Open($payload,[IO.Compression.ZipArchiveMode]::Create)
try{
    $count=0
    foreach($file in @($files)+(Get-Item -LiteralPath (Join-Path $stage 'package-manifest.json'))){
        $relative=$file.FullName.Substring($stage.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $count++;if($count%200 -eq 0){Write-Host ('Packaged '+$count+' files…')}
    }
}finally{$archive.Dispose()}
$checksum=Join-Path $run 'payload.sha256';(Get-FileHash -LiteralPath $payload).Hash | Set-Content -LiteralPath $checksum -Encoding ASCII
$setup=Join-Path $run 'AI-Center-Setup.exe'
& $csc /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ("/win32icon:"+(Join-Path $stage 'app.ico')) ("/out:"+$setup) ("/resource:"+$payload+",payload.zip") ("/resource:"+(Join-Path $PSScriptRoot 'Install.ps1')+",Install.ps1") ("/resource:"+$checksum+",payload.sha256") (Join-Path $PSScriptRoot 'Setup.cs')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$installerZip=Join-Path $run 'AI-Center-Installer.zip'
$archive=[IO.Compression.ZipFile]::Open($installerZip,[IO.Compression.ZipArchiveMode]::Create)
try{
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$setup,'AI-Center-Setup.exe',[IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $project 'README.md'),'README.md',[IO.Compression.CompressionLevel]::Optimal) | Out-Null
}finally{$archive.Dispose()}
[ordered]@{version='0.1.7';setup=$setup;setupSha256=(Get-FileHash -LiteralPath $setup).Hash;installerZip=$installerZip;zipSha256=(Get-FileHash -LiteralPath $installerZip).Hash;payloadSha256=(Get-FileHash -LiteralPath $payload).Hash;unsigned=$true;privateDataBundled=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'release-manifest.json') -Encoding UTF8
Write-Host ('PASS: release ready at '+$run)

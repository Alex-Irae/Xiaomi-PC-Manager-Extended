# Purpose: seal a new offline release after a setup-only fix without recompressing unchanged runtimes/models.
# Dependencies: PowerShell 7, .NET ZIP APIs, Windows .NET Framework compiler, a verified prior release.
# Outputs: a new numbered release with refreshed setup/source/development archives and package hashes.
# Command: pwsh -NoProfile -File tools\refresh_setup_release.ps1 -Previous packages\NNN_UTC -Destination packages\NEW_UTC
param([Parameter(Mandatory=$true)][string]$Previous,[Parameter(Mandatory=$true)][string]$Destination,[string]$StagedChanges)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$old=[IO.Path]::GetFullPath($Previous)
$release=[IO.Path]::GetFullPath($Destination)
if(!$old.StartsWith($root+'\packages\') -or !$release.StartsWith($root+'\packages\') -or (Test-Path -LiteralPath $release)){throw 'Use a new release directory inside packages.'}
New-Item -ItemType Directory -Path $release|Out-Null
$manifest=Get-Content -LiteralPath (Join-Path $old 'packages.json') -Raw|ConvertFrom-Json
function Hash($path){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
foreach($item in $manifest.components.PSObject.Properties){if((Hash (Join-Path $old $item.Value.payload)) -ne $item.Value.sha256){throw 'Prior archive checksum failed.'}}
foreach($file in Get-ChildItem -LiteralPath $old -File){if($file.Name -notin @('Xiaomi-Revamp-Installer.zip','SHA256SUMS.txt','public-assets.json')){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $release $file.Name)}}
function RefreshEntry($zip,$name,$source){
    $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Update)
    try{
        $oldEntry=$archive.GetEntry($name);if($oldEntry){$oldEntry.Delete()}
        $entry=$archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
        $stream=$entry.Open();try{$bytes=[IO.File]::ReadAllBytes($source);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    }finally{$archive.Dispose()}
}
$setup=Join-Path $root 'source\shared\Setup.cs'
foreach($component in $manifest.components.PSObject.Properties){
    $item=$component.Value
    foreach($name in @('Setup.cs','Setup.manifest')){
        $entry='Development/source/shared/'+$name;$sourceFile=Join-Path $root ('source/shared/'+$name)
        RefreshEntry (Join-Path $release $item.payload) $entry $sourceFile
        $item.files|Add-Member -NotePropertyName $entry -NotePropertyValue (Hash $sourceFile) -Force
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $root ('install\'+$item.folder+'\Development\source\shared\'+$name)) -Force
        RefreshEntry (Join-Path $release ($component.Name+'-development.zip')) $entry $sourceFile
    }
    $item.sha256=Hash (Join-Path $release $item.payload)
    Write-Output ('PASS: refreshed setup source in '+$component.Name+' payload and development ZIP')
}
if($StagedChanges){
    $changes=Get-Content -LiteralPath $StagedChanges -Raw|ConvertFrom-Json
    foreach($component in $changes.PSObject.Properties){
        $item=$manifest.components.($component.Name)
        $archive=[IO.Compression.ZipFile]::Open((Join-Path $release $item.payload),[IO.Compression.ZipArchiveMode]::Update)
        try{foreach($change in $component.Value.files){
            if(!$item.files.PSObject.Properties[$change.name] -or $change.name.Contains('..')){throw 'Unlisted staged change.'}
            $file=Join-Path $root ('install/'+$item.folder+'/'+$change.name)
            if((Hash $file) -ne $change.sha256){throw 'Staged change checksum failed.'}
            $archive.GetEntry($change.name).Delete();$entry=$archive.CreateEntry($change.name,[IO.Compression.CompressionLevel]::Optimal)
            $stream=$entry.Open();try{$bytes=[IO.File]::ReadAllBytes($file);$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
            $item.files.($change.name)=$change.sha256
            if($change.name.StartsWith('Development/')){RefreshEntry (Join-Path $release ($component.Name+'-development.zip')) $change.name $file}
        }}finally{$archive.Dispose()}
        $item.sha256=Hash (Join-Path $release $item.payload);$item.installedBytes=$component.Value.installedBytes
        Write-Output ('PASS: refreshed '+$component.Value.files.Count+' changed files for '+$component.Name)
    }
}
foreach($entry in @('README.md','source/shared/Setup.cs','source/shared/Setup.manifest','source/pc-manager/SuiteIntegration.cs','checks/check_setup.ps1','tools/refresh_setup_release.ps1','tools/build_suite.py')){RefreshEntry (Join-Path $release 'xiaomi-revamp-source.zip') $entry (Join-Path $root $entry)}
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $release 'README.md') -Force
$manifest.created=[DateTime]::UtcNow.ToString('O')
[IO.File]::WriteAllText((Join-Path $release 'packages.json'),($manifest|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach($item in @(@('Xiaomi-Revamp-Setup.exe','SUITE_SETUP'),@('AI-Center-Setup.exe','SEARCH_SETUP'),@('Screen-Translator-Setup.exe','TRANSLATOR_SETUP'),@('Uninstall.exe','UNINSTALL'))){
    $output=Join-Path $release $item[0]
    & $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/define:$($item[1])" "/win32manifest:$(Join-Path $root 'source/shared/Setup.manifest')" "/out:$output" $setup
    if($LASTEXITCODE -ne 0){throw 'Setup compilation failed.'}
}
Write-Output ('PASS: new setup release at '+$release)

# Purpose: preserve the last edited test source and verify every installed package file.
# Dependencies: administrator PowerShell, sealed release and existing installation.
# Outputs: an archive of changed development files, manifest and integrity evidence.
# Command: powershell -NoProfile -File tools\verify_installed_polish.ps1 -Release RELEASE -Output NEW_RESULTS
param([string]$Release,[string]$Output,[string]$InstallRoot='C:\Program Files\Xiaomi Revamp')
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Path $Output -Force|Out-Null
Start-Transcript -LiteralPath (Join-Path $Output 'integrity.log')|Out-Null
try{
    $manifest=Get-Content -LiteralPath (Join-Path $Release 'packages.json') -Raw|ConvertFrom-Json
    $root=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $target=Join-Path $root 'AI Center\Development\source\file-search\tests\check_frontend.cjs'
    $source=Join-Path (Split-Path -Parent $PSScriptRoot) 'source\file-search\tests\check_frontend.cjs'
    $expected=$manifest.components.'file-search'.files.'Development/source/file-search/tests/check_frontend.cjs'
    if((Get-FileHash $source).Hash -ne $expected){throw 'Test source does not match sealed release'}
    if((Get-FileHash $target).Hash -ne $expected){
        $archive=Join-Path $root ('AI Center\Development\Archives\fixture-before-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
        New-Item -ItemType Directory -Path $archive|Out-Null
        Copy-Item -LiteralPath $target -Destination $archive
        Copy-Item -LiteralPath $source -Destination $target -Force
    }
    $counts=@{};$verified=0
    foreach($component in $manifest.components.psobject.Properties){
        $app=Join-Path $root $component.Value.folder
        foreach($file in $component.Value.files.psobject.Properties){
            $path=[IO.Path]::GetFullPath((Join-Path $app $file.Name))
            if(!$path.StartsWith($app+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path outside component'}
            if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.Value){throw ('Installed checksum failed: '+$component.Name+'/'+$file.Name)}
            $verified++
            if($verified%3000 -eq 0){Write-Output ('PASS: '+$verified+' installed file hashes')}
        }
        $counts[$component.Name]=@($component.Value.files.psobject.Properties).Count
    }
    $owned=Get-Content -LiteralPath (Join-Path $root 'suite-owned.json') -Raw|ConvertFrom-Json
    foreach($component in $manifest.components.psobject.Properties){$owned|Add-Member -NotePropertyName $component.Name -NotePropertyValue $component.Value -Force}
    [IO.File]::WriteAllText((Join-Path $root 'suite-owned.json'),($owned|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
    @{passed=$true;files=$verified;components=$counts;release=(Split-Path -Leaf $Release);userDataTouched=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $Output 'summary.json') -Encoding UTF8
}catch{
    ($_|Out-String)|Set-Content -LiteralPath (Join-Path $Output 'error.txt');throw
}finally{Stop-Transcript|Out-Null}

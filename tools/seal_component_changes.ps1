# Purpose: refresh explicitly selected app payloads and their development copies.
# Dependencies: PowerShell 7 and existing verified ZIPs. Outputs: selected ZIPs and packages.json.
# Command: pwsh -NoProfile -File tools\seal_component_changes.ps1 -Release NEW_RELEASE -Changes changes.json
param([Parameter(Mandatory=$true)][string]$Release,[Parameter(Mandatory=$true)][string]$Changes)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$manifest=Get-Content -LiteralPath (Join-Path $Release 'packages.json') -Raw|ConvertFrom-Json -AsHashtable
$componentUpdates=Get-Content -LiteralPath $Changes -Raw|ConvertFrom-Json -AsHashtable
foreach($component in $componentUpdates.Keys){
    if($component -notin @('pc-manager','file-search','screen-translator')){throw 'Unknown app component.'}
    $item=$manifest.components[$component]
    $payload=Join-Path $Release $item.payload
    $development=Join-Path $Release ($component+'-development.zip')
    foreach($path in @($payload,$development)){
        $archive=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Update)
        try{foreach($name in $componentUpdates[$component].Keys){
            if($path -eq $development -and !$name.StartsWith('Development/')){continue}
            $source=[IO.Path]::GetFullPath($componentUpdates[$component][$name])
            if(![IO.File]::Exists($source)){throw ('Missing staged source: '+$source)}
            $entry=$archive.GetEntry($name)
            if($entry){if($path -eq $payload){$item.installedBytes-=$entry.Length};$entry.Delete()}
            $entry=$archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
            $input=[IO.File]::OpenRead($source);$output=$entry.Open()
            try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}
            if($path -eq $payload){$item.installedBytes+=(Get-Item -LiteralPath $source).Length;$item.files[$name]=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()}
        }}finally{$archive.Dispose()}
    }
    $item.sha256=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output ('PASS: refreshed '+$componentUpdates[$component].Count+' files in '+$component)
}
$manifest.created=[DateTime]::UtcNow.ToString('O')
[IO.File]::WriteAllText((Join-Path $Release 'packages.json'),($manifest|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))

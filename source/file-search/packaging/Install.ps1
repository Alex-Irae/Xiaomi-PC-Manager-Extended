# Purpose: verify and install an offline payload at a selected location.
# Dependencies: Windows PowerShell 5.1/.NET Framework. Outputs: files,
# shortcuts and uninstall entry; settings/index remain in each user's AppData.
# Command: called by AI-Center-Setup.exe; direct use requires package SHA256.
param([Parameter(Mandatory=$true)][string]$Package,[Parameter(Mandatory=$true)][string]$ExpectedHash,
    [ValidateSet('User','AllUsers')][string]$Scope='User',[Parameter(Mandatory=$true)][string]$Target,[switch]$NoIntegrations)
$ErrorActionPreference='Stop'
$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules')
Add-Type -AssemblyName System.IO.Compression.FileSystem
function FileHash([string]$Path){
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
}
if((FileHash $Package) -ne $ExpectedHash){throw 'Setup payload checksum mismatch.'}
$destination=[IO.Path]::GetFullPath($Target).TrimEnd('\')
if($destination -eq [IO.Path]::GetPathRoot($destination).TrimEnd('\') -or
    $destination -in @($env:ProgramFiles,[Environment]::GetEnvironmentVariable('ProgramFiles(x86)'),$env:WINDIR,$env:USERPROFILE,$env:LOCALAPPDATA)){throw 'Choose a dedicated application folder.'}
if($Scope -eq 'AllUsers' -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'All-users installation requires administrator approval.'}
$ancestor=$destination
while($ancestor){
    if(Test-Path -LiteralPath $ancestor){if((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Installation through a junction or symlink is not supported.'}}
    $parent=Split-Path -Parent $ancestor;if($parent -eq $ancestor){break};$ancestor=$parent
}
if(Test-Path -LiteralPath $destination){
    $prior=Join-Path $destination 'package-manifest.json'
    if((Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) -and (!(Test-Path -LiteralPath $prior) -or (Get-Content -LiteralPath $prior -Raw | ConvertFrom-Json).appId -ne 'LocalAICenter')){throw 'This folder contains unrelated files. Choose an empty folder or an existing AI Center installation.'}
}
$zip=[IO.Compression.ZipFile]::OpenRead($Package)
try {
    $entry=$zip.GetEntry('package-manifest.json');if(!$entry){throw 'Package manifest missing.'}
    $reader=New-Object IO.StreamReader($entry.Open());try{$manifest=$reader.ReadToEnd() | ConvertFrom-Json}finally{$reader.Dispose()}
    if($manifest.appId -ne 'LocalAICenter'){throw 'Unexpected package identity.'}
    foreach($item in $zip.Entries){
        $resolved=[IO.Path]::GetFullPath((Join-Path $destination $item.FullName))
        if(!$resolved.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe archive path.'}
        if($item.FullName.Contains(':') -or $item.FullName.StartsWith('/') -or $item.FullName.StartsWith('\')){throw 'Unsafe archive entry.'}
    }
    # Verify the complete payload before modifying a previous installation.
    foreach($file in $manifest.files){
        $item=$zip.GetEntry($file.path);if(!$item){throw ('Missing file: '+$file.path)}
        $stream=$item.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$actual=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
        if($actual -ne $file.sha256){throw ('File checksum mismatch: '+$file.path)}
    }
    Write-Output 'PASS: payload and file checksums verified.'
    if(Test-Path -LiteralPath (Join-Path $destination 'AI Center.exe')){
        # PowerShell does not wait for a GUI executable invoked with &. Wait for
        # the launcher and its command host so the probe cannot lock its own DLL.
        Start-Process -FilePath (Join-Path $destination 'AI Center.exe') -ArgumentList '--quit' -WindowStyle Hidden -Wait
    }
    # Refuse an in-use runtime before copying any new application files. CLI
    # indexing jobs can outlive the native window and hold Python/DLLs open.
    foreach($file in $manifest.files){
        $existing=Join-Path $destination $file.path
        if(Test-Path -LiteralPath $existing){
            $deadline=[DateTime]::UtcNow.AddSeconds(5)
            while($true){
                try{$probe=[IO.File]::Open($existing,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read);$probe.Dispose();break}
                catch{
                    # The resident UI receives quit asynchronously and may still
                    # be releasing its owned backend. Never copy while locked.
                    if([DateTime]::UtcNow -ge $deadline){throw ('Application file is in use or not writable: '+$existing+'. Close AI Center and CLI indexing jobs before updating. '+$_.Exception.Message)}
                    Start-Sleep -Milliseconds 100
                }
            }
        }
    }
    $backup=$null
    if(Test-Path -LiteralPath (Join-Path $destination 'package-manifest.json')){
        $backup=$destination+'.backup-'+(Get-Date -Format yyyyMMddTHHmmssfff)
        New-Item -ItemType Directory -Path $backup | Out-Null
        Get-ChildItem -LiteralPath $destination -Force | Copy-Item -Destination $backup -Recurse
        Write-Output ('Preserved previous installation: '+$backup)
    }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $count=0
    foreach($item in $zip.Entries){
        $resolved=[IO.Path]::GetFullPath((Join-Path $destination $item.FullName))
        if(!$item.Name){New-Item -ItemType Directory -Force -Path $resolved | Out-Null;continue}
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolved) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item,$resolved,$true)
        $count++;if($count%200 -eq 0){Write-Output ('Installed '+$count+' files…')}
    }
}finally{$zip.Dispose()}
Set-Content -LiteralPath (Join-Path $destination 'install-scope.txt') -Value $Scope -Encoding ASCII
if(!$NoIntegrations){
    $registry=if($Scope -eq 'AllUsers'){'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAICenter'}else{'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAICenter'}
    New-Item -Path $registry -Force | Out-Null
    $properties=@{DisplayName='Local AI Center';DisplayVersion=$manifest.version;Publisher='Local AI Center';InstallLocation=$destination;DisplayIcon=(Join-Path $destination 'AI Center.exe');UninstallString=('"'+(Join-Path $destination 'Uninstall AI Center.exe')+'"');NoModify=1;NoRepair=1}
    foreach($key in $properties.Keys){New-ItemProperty -Path $registry -Name $key -Value $properties[$key] -PropertyType $(if($properties[$key] -is [int]){'DWord'}else{'String'}) -Force | Out-Null}
    $programs=[Environment]::GetFolderPath($(if($Scope -eq 'AllUsers'){[Environment+SpecialFolder]::CommonPrograms}else{[Environment+SpecialFolder]::Programs}))
    $menu=Join-Path $programs 'Local AI Center';New-Item -ItemType Directory -Force -Path $menu | Out-Null
    $shell=New-Object -ComObject WScript.Shell
    foreach($shortcut in @(@('AI Center','AI Center.exe','--center'),@('File Search','AI Center.exe','--search'),@('Uninstall AI Center','Uninstall AI Center.exe',''))){
        $link=$shell.CreateShortcut((Join-Path $menu ($shortcut[0]+'.lnk')));$link.TargetPath=Join-Path $destination $shortcut[1];$link.Arguments=$shortcut[2];$link.WorkingDirectory=$destination;$link.Save()
    }
    $desktop=[Environment]::GetFolderPath($(if($Scope -eq 'AllUsers'){[Environment+SpecialFolder]::CommonDesktopDirectory}else{[Environment+SpecialFolder]::DesktopDirectory}))
    $link=$shell.CreateShortcut((Join-Path $desktop 'AI Center.lnk'));$link.TargetPath=Join-Path $destination 'AI Center.exe';$link.Arguments='--center';$link.WorkingDirectory=$destination;$link.Save()
}
@{scope=$Scope;target=$destination;version=$manifest.version;completedAt=(Get-Date).ToString('o');integrations=[bool](!$NoIntegrations);previousInstallBackup=$backup} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'install-report.json') -Encoding UTF8
Write-Output ('PASS: installed '+$manifest.version+' at '+$destination)

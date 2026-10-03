# Purpose: complete app/profile/integration removal. Dependencies: PowerShell 5.1.
# Output: removes only dedicated AI Center paths; shared dependencies untouched.
# Command: Uninstall AI Center.exe (or powershell -File Uninstall.ps1 -Target APP).
param([Parameter(Mandatory=$true)][string]$Target,[switch]$NoIntegrations,[switch]$NoProfiles)
$ErrorActionPreference='Stop'
$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules')
$root=[IO.Path]::GetFullPath($Target).TrimEnd('\')
$manifest=Get-Content -LiteralPath (Join-Path $root 'package-manifest.json') -Raw | ConvertFrom-Json
if($manifest.appId -ne 'LocalAICenter'){throw 'Unexpected installation identity.'}
if($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\') -or $root -in @($env:ProgramFiles,$env:WINDIR,$env:USERPROFILE,$env:LOCALAPPDATA)){throw 'Refusing a non-dedicated app directory.'}
function RemoveOwnedTree([string]$Path,[string]$AllowedParent){
    $absolute=[IO.Path]::GetFullPath($Path).TrimEnd('\');$parent=[IO.Path]::GetFullPath($AllowedParent).TrimEnd('\')
    if(!$absolute.StartsWith($parent+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Parent $absolute) -ne $parent){throw 'Unsafe removal target.'}
    if(!(Test-Path -LiteralPath $absolute)){return}
    function RemoveNode([string]$Node){
        $item=Get-Item -LiteralPath $Node -Force
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$item.Delete();return}
        if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $Node -Force){RemoveNode $child.FullName}}
        # Delete the verified empty node directly. PowerShell provider removal
        # can request hidden interactive confirmation for Chromium cache folders.
        if($item.Attributes -band [IO.FileAttributes]::ReadOnly){$item.Attributes=$item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)}
        $item.Delete()
    }
    Write-Output ('Removing '+$absolute)
    RemoveNode $absolute
    Write-Output ('Removed '+$absolute)
}
$ancestor=$root
while($ancestor){
    if((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing an installation through a junction.'}
    $parent=Split-Path -Parent $ancestor;if(!$parent -or $parent -eq $ancestor){break};$ancestor=$parent
}
$scope=(Get-Content -LiteralPath (Join-Path $root 'install-scope.txt') -Raw).Trim()
if($scope -eq 'AllUsers' -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator approval is required.'}
Start-Process -FilePath (Join-Path $root 'AI Center.exe') -ArgumentList '--quit' -WindowStyle Hidden -Wait
$deadline=[DateTime]::UtcNow.AddSeconds(25)
while(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)}){
    if([DateTime]::UtcNow -ge $deadline){throw 'AI Center still has an owned process running. Nothing has been removed.'}
    Start-Sleep -Milliseconds 100
}
$profiles=@($env:USERPROFILE)
if($scope -eq 'AllUsers'){$profiles+=@(Get-CimInstance Win32_UserProfile | Where-Object {!$_.Special -and $_.LocalPath} | ForEach-Object {$_.LocalPath})}
$profiles=@($profiles | Select-Object -Unique)
if(!$NoIntegrations){
    foreach($registry in @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAICenter','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\LocalAICenter','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LocalAICenter')){
        if((Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue).InstallLocation -eq $root){Remove-Item -LiteralPath $registry}
    }
    $runKeys=@('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run')
    if($scope -eq 'AllUsers'){$runKeys+=@(Get-ChildItem Registry::HKEY_USERS | Where-Object {$_.PSChildName -match '^S-1-5-21-[\d-]+$'} | ForEach-Object {'Registry::'+$_.Name+'\Software\Microsoft\Windows\CurrentVersion\Run'})}
    foreach($key in $runKeys){
        $value=(Get-ItemProperty -LiteralPath $key -Name LocalAICenter -ErrorAction SilentlyContinue).LocalAICenter
        if($value -and $value.Contains('"'+(Join-Path $root 'AI Center.exe')+'"')){Remove-ItemProperty -LiteralPath $key -Name LocalAICenter}
    }
    $menus=@([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms),[Environment]::GetFolderPath([Environment+SpecialFolder]::Programs))
    $desktops=@([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonDesktopDirectory),[Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory))
    foreach($profile in $profiles){$menus+=Join-Path $profile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs';$desktops+=Join-Path $profile 'Desktop'}
    $shell=New-Object -ComObject WScript.Shell
    $links=@($desktops | ForEach-Object {Join-Path $_ 'AI Center.lnk'})
    foreach($menu in $menus){foreach($name in 'AI Center','File Search','Uninstall AI Center'){$links+=Join-Path $menu ('Local AI Center\'+$name+'.lnk')}}
    foreach($path in ($links | Select-Object -Unique)){
        if(Test-Path -LiteralPath $path){$link=$shell.CreateShortcut($path);if($link.TargetPath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $path}}
    }
    foreach($menu in ($menus | Select-Object -Unique)){
        $folder=Join-Path $menu 'Local AI Center'
        if((Test-Path -LiteralPath $folder) -and !(Get-ChildItem -LiteralPath $folder -Force | Select-Object -First 1)){Remove-Item -LiteralPath $folder}
    }
}
if(!$NoProfiles){
    foreach($profile in $profiles){
        foreach($relative in 'AppData\Local','AppData\Roaming'){$parent=Join-Path $profile $relative;RemoveOwnedTree (Join-Path $parent 'LocalAICenter') $parent}
        $packages=Join-Path $profile 'AppData\Local\Packages'
        if(Test-Path -LiteralPath $packages){foreach($package in Get-ChildItem -LiteralPath $packages -Directory -Filter 'OpenAI.Codex_*'){
            foreach($relative in 'LocalCache\Local','LocalCache\Roaming'){$parent=Join-Path $package.FullName $relative;RemoveOwnedTree (Join-Path $parent 'LocalAICenter') $parent}
        }}
    }
}
$parent=Split-Path -Parent $root
foreach($backup in Get-ChildItem -LiteralPath $parent -Directory | Where-Object {$_.Name -match ('^'+[regex]::Escape((Split-Path -Leaf $root))+'\.backup-\d{8}T\d{9}$')}){
    $identity=Join-Path $backup.FullName 'package-manifest.json'
    if((Test-Path -LiteralPath $identity) -and (Get-Content -LiteralPath $identity -Raw | ConvertFrom-Json).appId -eq 'LocalAICenter'){RemoveOwnedTree $backup.FullName $parent}
}
RemoveOwnedTree $root $parent
Write-Output 'PASS: AI Center, private profiles and owned integrations removed. Shared dependencies untouched.'

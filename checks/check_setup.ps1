# Purpose: exercise real setup transactions, configurable protected data paths and per-app removal.
# Dependencies: administrator PowerShell 5.1 and Windows .NET Framework compiler, no installed app launch.
# Outputs: configuration, operation logs and summary.json in a new RunDirectory.
# Command: powershell -NoProfile -File checks\check_setup.ps1 -RunDirectory results\NNN_UTC_seed0
param([Parameter(Mandatory=$true)][string]$RunDirectory,[switch]$ContentStore)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
if(Test-Path -LiteralPath $run){throw 'Use a new result directory.'}
New-Item -ItemType Directory -Path $run|Out-Null
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\source\shared\Setup.cs'))
$fixtureRegistry='Software\XiaomiRevampSetupTests\'+(Split-Path -Leaf $run)
$compiledSource=Join-Path $run 'Setup-fixture.cs'
# Isolate shell destinations and registry namespaces. Test app names and owned
# shortcut behavior without modifying real Start Menu/desktop or uninstall keys.
$testSource=[IO.File]::ReadAllText($source)
$testSource=$testSource.Replace('Environment.GetFolderPath(all ? Environment.SpecialFolder.CommonStartMenu : Environment.SpecialFolder.StartMenu)','Path.Combine(Path.GetDirectoryName(root), "ShortcutFixture")')
$testSource=$testSource.Replace('Environment.GetFolderPath(all ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory)','Path.Combine(Path.GetDirectoryName(root), "DesktopFixture")')
$testSource=$testSource.Replace('Software\Microsoft\Windows\CurrentVersion\Uninstall\XiaomiRevamp.',($fixtureRegistry+'\Uninstall\XiaomiRevamp.')).Replace('Software\Microsoft\Windows\CurrentVersion\Run',($fixtureRegistry+'\Run'))
[IO.File]::WriteAllText($compiledSource,$testSource)
$root=Join-Path $run 'Installed'
$data=Join-Path $run 'Chosen Data'
$protectedData=Join-Path $env:ProgramFiles ('XiaomiRevamp-Data-Check-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
@{seed=0;mode='real-offline-installer';installRoot=$root;dataRoot=$data;searchData=$protectedData;allUsers=$true;modelsInitialized=$false;desktopCaptured=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'config.json')
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$script:checkCount=0
function Assert($condition,$message){if(!$condition){throw $message};$script:checkCount++;Write-Output ('PASS: '+$message)}
function Zip($path,$entries){
    $archive=[IO.Compression.ZipFile]::Open($path,'Create')
    try{foreach($key in $entries.Keys){$entry=$archive.CreateEntry($key);$writer=New-Object IO.StreamWriter($entry.Open(),(New-Object Text.UTF8Encoding($false)));try{$writer.Write($entries[$key])}finally{$writer.Dispose()}}}finally{$archive.Dispose()}
}
function Hash($path){return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Apply($arguments,$expect=0){
    $log=Join-Path $run ('operation-'+[DateTime]::UtcNow.ToString('HHmmssfffffff')+'.log')
    $quoted=($arguments+@('--log',$log)|ForEach-Object{ '"'+$_+'"'}) -join ' '
    $process=Start-Process -FilePath (Join-Path $run 'Xiaomi-Revamp-Setup.exe') -ArgumentList $quoted -WindowStyle Hidden -PassThru -Wait
    Assert ($process.ExitCode -eq $expect) ('setup exit code '+$expect)
}
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable=Join-Path $run 'Xiaomi-Revamp-Setup.exe'
& $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /define:SUITE_SETUP "/win32manifest:$(Join-Path ([IO.Path]::GetDirectoryName($source)) 'Setup.manifest')" "/out:$executable" $compiledSource
if($LASTEXITCODE -ne 0){throw 'Setup compilation failed.'}
Copy-Item -LiteralPath $executable -Destination (Join-Path $run 'Uninstall.exe')
$components=@{}
foreach($component in @('file-search','screen-translator')){
    $folder=if($component -eq 'file-search'){'AI Center'}else{'Screen Translator'}
    $entries=@{'suite-component.json'=('{"schema":1,"component":"'+$component+'"}');'runtime/check.txt'='original';'Development/source/edit.txt'='original development'}
    if($component -eq 'file-search'){$entries['config.example.json']='{"roots":[],"excluded_folders":[],"model_path":"models/qwen3-embedding"}'}
    $payload=Join-Path $run ($component+'.zip');Zip $payload $entries
    $hashes=@{};foreach($key in $entries.Keys){$hashes[$key]=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($entries[$key])))).Replace('-','').ToLowerInvariant()}
    $components[$component]=@{folder=$folder;payload=($component+'.zip');sha256=(Hash $payload);files=$hashes}
}
$longEntry='sdk/'+(('long-directory/'*12))+'a-long-compiler-task-resource-file.txt'
Zip (Join-Path $run 'development-toolchain.zip') @{'sdk/README.txt'='fixture toolchain';$longEntry='long-path fixture'}
$package=@{schema=1;version='0.2.0';components=$components;developmentToolchain=@{sha256=(Hash (Join-Path $run 'development-toolchain.zip'))}}
if($ContentStore){
    $blobs=@{}; $toolFiles=@{}
    foreach($component in $components.Values){
        $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $run $component.payload))
        try{foreach($entry in $archive.Entries){$reader=New-Object IO.StreamReader($entry.Open());try{$blobs[$component.files[$entry.FullName]]=$reader.ReadToEnd()}finally{$reader.Dispose()}}}finally{$archive.Dispose()}
    }
    $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $run 'development-toolchain.zip'))
    try{foreach($entry in $archive.Entries){$reader=New-Object IO.StreamReader($entry.Open());try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()};$digest=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','').ToLowerInvariant();$blobs[$digest]=$text;$toolFiles[$entry.FullName]=$digest}}finally{$archive.Dispose()}
    Zip (Join-Path $run 'payload.zip') $blobs
    $package.contentStore=@{payload='payload.zip';sha256=(Hash (Join-Path $run 'payload.zip'))}
    $package.developmentToolchain.files=$toolFiles
}
$package|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $run 'packages.json')
try{
    Apply @('--apply','--target',$root,'--components','file-search,screen-translator','--data-root',$data,'--search-data',$protectedData,'--all-users','--development')
    $menu=Join-Path $run 'ShortcutFixture\Programs\Xiaomi Revamp'
    $shell=New-Object -ComObject WScript.Shell
    $centerLink=$shell.CreateShortcut((Join-Path $menu 'AI Center.lnk'))
    $searchLink=$shell.CreateShortcut((Join-Path $menu 'File Search.lnk'))
    Assert ($centerLink.Arguments -eq '--center' -and $searchLink.Arguments -eq '--search') 'AI Center opens launcher and File Search opens the bar'
    $oldLink=$shell.CreateShortcut((Join-Path $menu 'AI Center settings.lnk'));$oldLink.TargetPath=Join-Path $root 'AI Center\AI Center.exe';$oldLink.Arguments='--center';$oldLink.Save()
    $marker=Get-Content -LiteralPath (Join-Path $root 'suite-install.json') -Raw|ConvertFrom-Json
    $searchPath=$marker.componentData.'file-search'.Replace('{sid}',$sid)
    $translatorPath=$marker.componentData.'screen-translator'.Replace('{sid}',$sid)
    Assert ($searchPath.StartsWith($protectedData)) 'AI Center data uses the chosen Program Files subfolder'
    Assert (Test-Path -LiteralPath (Join-Path $searchPath '.revamp-data.json')) 'data has verified per-app ownership'
    Assert ([IO.File]::ReadAllText((Join-Path $root ('AI Center\Development\toolchain\'+$longEntry))) -eq 'long-path fixture') 'development toolchain extracts paths beyond the legacy Windows length limit'
    $acl=Get-Acl -LiteralPath $searchPath
    Assert ([bool]($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object{($_.IdentityReference.Value -eq 'S-1-5-32-545') -and (($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify)})) 'data permits normal users to modify it while app files stay separate'
    $dev=Join-Path $root 'AI Center\Development\source\edit.txt'
    [IO.File]::WriteAllText($dev,'my development edit')
    Apply @('--apply','--target',$root,'--components','file-search','--all-users','--development')
    Assert (!(Test-Path (Join-Path $menu 'AI Center settings.lnk'))) 'upgrade removes owned legacy settings shortcut'
    Assert ((Get-Content -LiteralPath $dev -Raw) -eq 'my development edit') 'upgrade preserves editable development source'
    [IO.File]::WriteAllText((Join-Path $root 'AI Center\added.txt'),'added')
    Apply @('--apply','--action','remove','--target',$root,'--components','file-search','--remove-data','--remove-changed')
    Assert (!(Test-Path (Join-Path $menu 'AI Center.lnk')) -and !(Test-Path (Join-Path $menu 'File Search.lnk'))) 'uninstall removes both owned AI Center shortcuts'
    Assert (!(Test-Path -LiteralPath $searchPath)) 'requested search settings and data were removed'
    Assert (Test-Path -LiteralPath $translatorPath) 'unselected translator settings were retained'
    Assert ((Get-Content -LiteralPath $dev -Raw) -eq 'my development edit') 'uninstall preserves development when that removal option is off'
    Assert (!(Test-Path -LiteralPath (Join-Path $root 'AI Center\added.txt'))) 'requested changed and added app files were removed'
    Apply @('--apply','--target',$root,'--components','file-search','--all-users','--development')
    Assert ((Get-Content -LiteralPath $dev -Raw) -eq 'my development edit') 'reinstall works alongside retained development'
    $unrelated=Join-Path $run 'Unrelated Data'
    $existing=Join-Path $unrelated ('Users\'+$sid+'\file-search\keep.txt')
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($existing)) -Force|Out-Null
    [IO.File]::WriteAllText($existing,'keep this unrelated data')
    Apply @('--apply','--target',(Join-Path $run 'Refused Install'),'--components','file-search','--data-root',$unrelated) 1
    Assert ((Get-Content -LiteralPath $existing -Raw) -eq 'keep this unrelated data') 'unmarked existing data is refused and preserved'
    Apply @('--apply','--action','remove','--target',$root,'--components','file-search,screen-translator','--remove-data','--remove-development','--remove-changed')
    Assert (!(Test-Path -LiteralPath $dev)) 'explicit development removal was honored'
    # Recovery folders contain earlier app versions and are deliberately retained on upgrades.
    Assert (!(Test-Path -LiteralPath (Join-Path $root 'AI Center'))) 'selected app folders were removed'
    Assert (!(Test-Path -LiteralPath $root)) 'full removal also clears verified recovery and staging folders'
    $uninstall=@(Get-ChildItem ('HKLM:\'+$fixtureRegistry+'\Uninstall') -ErrorAction SilentlyContinue|Get-ItemProperty|Where-Object{$_.InstallLocation -like ($root+'*')})
    Assert ($uninstall.Count -eq 0) 'all-users uninstall registry entries were removed'
    @{passed=$true;checks=$script:checkCount;installUpgradeUninstall=$true;programFilesData=$true;allUsersRegistration=$true;developmentPreserved=$true;modelsInitialized=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'summary.json')
}catch{
    @{passed=$false;error=$_.Exception.ToString()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'summary.json');throw
}

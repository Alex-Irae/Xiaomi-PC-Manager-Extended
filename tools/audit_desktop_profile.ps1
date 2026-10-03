# Purpose: capture live desktop-profile status after the installed integration checks.
# Dependencies: Windows PowerShell, existing installation. Outputs: private status/log snapshots.
# Command: run this helper in the normal interactive desktop task with -Output NEW_RESULTS.
param([string]$Output,[string]$InstallRoot='C:\Program Files\Xiaomi Revamp')
$ErrorActionPreference='Stop'
trap {($_|Out-String)|Set-Content (Join-Path $Output 'physical-error.txt');exit 1}
$marker=Get-Content -LiteralPath (Join-Path $InstallRoot 'suite-install.json') -Raw|ConvertFrom-Json
$data=if($marker.dataRoot){[Environment]::ExpandEnvironmentVariables($marker.dataRoot).Replace('{sid}',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)}else{Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('XiaomiRevamp\'+$marker.profileId)}
$states=@()
foreach($sample in 1..2){
    $snapshot=@{utc=[DateTime]::UtcNow.ToString('O')}
    foreach($component in @('pc-manager','file-search','screen-translator')){
        $state=Get-Content -LiteralPath (Join-Path $data ('shared\status-'+$component+'.json')) -Raw|ConvertFrom-Json
        if($component -ne 'screen-translator' -and (!(Get-Process -Id $state.Pid -ErrorAction SilentlyContinue) -or $state.Error)){throw ('Invalid live companion state: '+$component)}
        $snapshot[$component]=$state
    }
    $states+=$snapshot
    if($sample -eq 1){Start-Sleep -Seconds 12}
}
$states|ConvertTo-Json -Depth 15|Set-Content (Join-Path $Output 'live-status.json') -Encoding UTF8
$config=Get-Content -LiteralPath (Join-Path $data 'file-search\config.json') -Raw|ConvertFrom-Json
if($config.model_standby -ne 'keep_loaded' -or $config.indexing_mode -ne 'normal'){throw 'Search standby or indexing mode changed unexpectedly'}
$config|ConvertTo-Json -Depth 20|Set-Content (Join-Path $Output 'live-search-config.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $data 'file-search\data\backend.log') -Tail 140|Set-Content (Join-Path $Output 'search-backend-log.txt')
foreach($component in @('file-search','screen-translator')){
    $log=Join-Path $data ($component+'\'+$(if($component -eq 'file-search'){'data\native.log'}else{'native.log'}))
    if(Test-Path $log){Get-Content -LiteralPath $log -Tail 120|Set-Content (Join-Path $Output ($component+'-native.log'))}
}
$index=Get-Item -LiteralPath (Join-Path $data 'file-search\data\index.sqlite3.dpapi')
$stream=[IO.File]::Open($index.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
try{$header=New-Object byte[] 19;$null=$stream.Read($header,0,$header.Length)}finally{$stream.Dispose()}
@{passed=$true;indexBytes=$index.Length;indexUpdated=$index.LastWriteTimeUtc.ToString('O');protectedHeader=[Text.Encoding]::ASCII.GetString($header);nativePidsStable=$states[0].'file-search'.Pid -eq $states[1].'file-search'.Pid;standby=$config.model_standby;indexing=$config.indexing_mode}|ConvertTo-Json|Set-Content (Join-Path $Output 'summary.json') -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $Output 'physical-done.txt'),'done')

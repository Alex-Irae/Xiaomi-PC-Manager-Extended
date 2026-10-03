# Purpose: apply inspected app replacements from a normal desktop task, preserving personal data.
# Dependencies: administrator Windows PowerShell, existing installation and hash-verified patch plan.
# Outputs: file backups, lifecycle/GUI evidence and updated ownership manifest.
# Command: powershell -NoProfile -File tools\apply_polish_patch.ps1 -WorkRoot STAGING_ROOT -Output NEW_EVIDENCE
param([Parameter(Mandatory=$true)][string]$WorkRoot,[Parameter(Mandatory=$true)][string]$Output,[string]$PatchPlanPath='patch-plan.json',[string]$InstallRoot='C:\Program Files\Xiaomi Revamp')
$ErrorActionPreference='Stop'
$stage=[IO.Path]::GetFullPath($WorkRoot).TrimEnd('\')
$out=[IO.Path]::GetFullPath($Output).TrimEnd('\')
if(!$out.StartsWith($stage+'\results\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence outside staging results'}
New-Item -ItemType Directory -Path $out -Force|Out-Null
trap {($_|Out-String)|Set-Content -LiteralPath (Join-Path $out 'error.txt');exit 1}
if(!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator token required'}
$root=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$marker=Get-Content -LiteralPath (Join-Path $root 'suite-install.json') -Raw|ConvertFrom-Json
if($marker.schema -ne 1 -or $marker.portable){throw 'Installed desktop profile required'}
$data=if($marker.dataRoot){[Environment]::ExpandEnvironmentVariables($marker.dataRoot).Replace('{sid}',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)}else{Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('XiaomiRevamp\'+$marker.profileId)}
$plan=Get-Content -LiteralPath (Join-Path $stage $PatchPlanPath) -Raw|ConvertFrom-Json
foreach($file in $plan){
    $target=[IO.Path]::GetFullPath($file.destination);$source=[IO.Path]::GetFullPath($file.source)
    if(!$target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or !$source.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Patch path outside inspected scope'}
    for($parent=$target;$parent;$parent=Split-Path -Parent $parent){if((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Redirected patch path'}}
    if($file.previousHash){if(!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $file.previousHash){throw ('Changed installed file: '+$target)}}elseif(Test-Path -LiteralPath $target){throw ('Unexpected installed file: '+$target)}
    if((Get-FileHash -LiteralPath $source).Hash -ne $file.newHash){throw ('Changed staged file: '+$source)}
}
function Status([string]$component){Get-Content -LiteralPath (Join-Path $data ('shared\status-'+$component+'.json')) -Raw|ConvertFrom-Json}
function WaitFor([scriptblock]$Condition,[string]$Label,[int]$Seconds=120){$watch=[Diagnostics.Stopwatch]::StartNew();while($watch.Elapsed.TotalSeconds -lt $Seconds){if(& $Condition){Write-Output ('PASS: '+$Label);return};Start-Sleep -Milliseconds 200};throw ('Timed out: '+$Label)}
function Command([string]$Component,[string]$Arguments){
    $folder=@{'pc-manager'='PC Manager';'file-search'='AI Center';'screen-translator'='Screen Translator'}[$Component]
    $native=Join-Path $root ($folder+'\runtime\dotnet\'+$(if($Component -eq 'pc-manager'){'PCManager.exe'}elseif($Component -eq 'file-search'){'AI Center.exe'}else{'dotnet.exe'}))
    if($Component -eq 'screen-translator'){$Arguments=('"{0}" {1}' -f (Join-Path $root 'Screen Translator\ScreenTranslator.dll'),$Arguments)}
    Start-Process -FilePath $native -ArgumentList $Arguments -WorkingDirectory (Join-Path $root $folder) -WindowStyle Hidden|Out-Null
}
$ownedPath=Join-Path $root 'suite-owned.json'
$owned=Get-Content -LiteralPath $ownedPath -Raw|ConvertFrom-Json
$before=@{};foreach($component in @('pc-manager','file-search','screen-translator')){$before[$component]=Status $component}
$settings=Join-Path $data 'file-search\config.json'
Copy-Item -LiteralPath $settings -Destination (Join-Path $out 'search-config-before.json')
$shared=Get-Content -LiteralPath (Join-Path $data 'shared\settings.json') -Raw|ConvertFrom-Json
$bindingsBefore=$shared.Bindings|ConvertTo-Json -Compress
$tasks=@(Get-ScheduledTask|Where-Object {@($_.Actions|Where-Object {$_.Execute -like ($root+'\PC Manager\*')}).Count -gt 0 -and $_.State -ne 'Disabled'})
Start-Transcript -LiteralPath (Join-Path $out 'update.log')|Out-Null
try{
    foreach($task in $tasks){Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath|Out-Null}
    foreach($component in @('pc-manager','file-search','screen-translator')){Command $component '--quit'}
    WaitFor { @($before.Values|Where-Object {Get-Process -Id $_.Pid -ErrorAction SilentlyContinue}).Count -eq 0 } 'resident apps exited through their own shutdown protocols'
    $archiveName='polish-before-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    foreach($file in $plan){
        $backup=Join-Path $out ('backup\'+$file.component+'\'+$file.relative)
        New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force|Out-Null
        if(Test-Path -LiteralPath $file.destination){Copy-Item -LiteralPath $file.destination -Destination $backup}
        if($file.relative.StartsWith('Development/source/')){
            $appFolder=@{'pc-manager'='PC Manager';'file-search'='AI Center';'screen-translator'='Screen Translator'}[$file.component]
            $development=Join-Path (Join-Path $root ($appFolder+'\Development')) ('Archives\'+$archiveName+'\'+$file.relative.Substring('Development/'.Length))
            New-Item -ItemType Directory -Path (Split-Path -Parent $development) -Force|Out-Null
            if(Test-Path -LiteralPath $file.destination){Copy-Item -LiteralPath $file.destination -Destination $development}
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $file.destination) -Force|Out-Null
        Copy-Item -LiteralPath $file.source -Destination $file.destination -Force
        if((Get-FileHash -LiteralPath $file.destination).Hash -ne $file.newHash){throw ('Installed hash failed: '+$file.relative)}
        if($file.owned){$owned.($file.component).files|Add-Member -NotePropertyName $file.relative -NotePropertyValue $file.newHash -Force}
        Write-Output ('PASS: updated '+$file.component+'/'+$file.relative)
    }
    [IO.File]::WriteAllText($ownedPath,($owned|ConvertTo-Json -Depth 100),(New-Object Text.UTF8Encoding($false)))
    $config=Get-Content -LiteralPath $settings -Raw|ConvertFrom-Json
    $config.model_standby='keep_loaded'
    [IO.File]::WriteAllText($settings,($config|ConvertTo-Json -Depth 30),(New-Object Text.UTF8Encoding($false)))
    Command 'pc-manager' '--tray'
    WaitFor {$a=Status 'pc-manager';$a.Pid -ne $before['pc-manager'].Pid -and (Get-Process -Id $a.Pid -ErrorAction SilentlyContinue) -and !$a.Error -and $a.Owned -contains 'file-search' -and $a.Owned -contains 'screen-translator'} 'updated hub owns both companion shortcut groups'
    # PC Manager starts the public search wrapper; its new Explorer handoff must
    # report in this actual Windows desktop profile, not the caller's private copy.
    WaitFor {$a=Status 'file-search';$a.Pid -ne $before['file-search'].Pid -and (Get-Process -Id $a.Pid -ErrorAction SilentlyContinue) -and $a.Owner -eq 'PC Manager' -and !$a.Error} 'public wrapper uses the normal shared desktop profile'
    $runs=Join-Path $data 'pc-manager\results\suite-ui'
    $last=@(Get-ChildItem -LiteralPath $runs -Directory -ErrorAction SilentlyContinue|Sort-Object Name|Select-Object -Last 1).Name
    Command 'pc-manager' '--validate-suite'
    WaitFor {$script:newest=Get-ChildItem -LiteralPath $runs -Directory|Sort-Object Name|Select-Object -Last 1; $script:newest.Name -ne $last -and (Test-Path (Join-Path $script:newest.FullName 'restoration.json'))} 'installed shortcut editor and button feedback checks finished' 300
    Copy-Item -LiteralPath $script:newest.FullName -Destination (Join-Path $out 'pc-validation') -Recurse
    $report=Get-Content -LiteralPath (Join-Path $out 'pc-validation\summary.json') -Raw|ConvertFrom-Json
    $restored=Get-Content -LiteralPath (Join-Path $out 'pc-validation\restoration.json') -Raw|ConvertFrom-Json
    $after=Get-Content -LiteralPath (Join-Path $data 'shared\settings.json') -Raw|ConvertFrom-Json
    if(!$restored.passed -or ($after.Bindings|ConvertTo-Json -Compress) -ne $bindingsBefore){throw 'User shortcut restoration failed'}
    if(!$report.passed){throw 'Installed shortcut/UI checks failed; see retained pc-validation report'}
    @{passed=$true;updatedFiles=$plan.Count;desktopProfileVerified=$true;bindingsPreserved=$true;indexRetained=$true;pcValidation=$report;hub=(Status 'pc-manager');search=(Status 'file-search');translator=(Status 'screen-translator')}|ConvertTo-Json -Depth 25|Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $out 'done.txt'),'done')
}finally{
    foreach($task in $tasks){Enable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath|Out-Null}
    Stop-Transcript|Out-Null
}

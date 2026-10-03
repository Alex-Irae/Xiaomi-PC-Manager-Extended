# Purpose: run the native shortcut/English-pixel check in an isolated portable profile.
# Dependencies: existing translator private .NET/Python, models and a previously built native folder.
# Outputs: new config, fixtures, logs and JSON; no desktop captures or real profile changes.
# Command: powershell -NoProfile -File checks\run_translation_shortcuts.ps1 -Native BUILD -Source SOURCE -App INSTALLED_APP -CacheProfile MODEL_CACHE_PROFILE -RunDirectory NEW_RUN
param([Parameter(Mandatory=$true)][string]$Native,[Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$App,[Parameter(Mandatory=$true)][string]$CacheProfile,[Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$app=[IO.Path]::GetFullPath($App)
$cache=[IO.Path]::GetFullPath($CacheProfile)
$Source=(Resolve-Path -LiteralPath $Source).Path
$Native=(Resolve-Path -LiteralPath $Native).Path
$run=[IO.Path]::GetFullPath($RunDirectory)
if(Test-Path -LiteralPath $run){throw 'Choose a new results directory'}
$instance=Join-Path $run 'instance'
$component=Join-Path $instance 'Screen Translator'
$data=Join-Path $instance '_data\shared'
New-Item -ItemType Directory -Path $component,$data|Out-Null
Get-ChildItem -LiteralPath $Native -File|Copy-Item -Destination $component
@{schema=1;profileId='shortcut-check';portable=$true}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $instance 'suite-install.json') -Encoding UTF8
$bindings=@{'file-search.open'='none';'screen-translator.toggle'='Ctrl+Alt+Shift+F18';'screen-translator.screen'='Ctrl+Alt+Shift+F19';'screen-translator.region'='Ctrl+Alt+Shift+F20';'screen-translator.original'='Ctrl+Alt+Shift+F21';'screen-translator.filter'='Ctrl+Alt+Shift+F22'}
@{Schema=1;Revision=1;Bindings=$bindings;Reservations=@{};PausedUntil=@{};Commands=@{};Theme='system';Accent='#3482ff';SharedAppearance=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $data 'settings.json') -Encoding UTF8
@{models=(Join-Path $app 'models\zh-en');shortcut=$bindings['screen-translator.toggle'];autostart=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'config.json') -Encoding UTF8
$arguments=@(('"{0}"' -f (Join-Path $component 'ScreenTranslator.dll')),'--root',('"{0}"' -f $Source),'--python',('"{0}"' -f (Join-Path $app 'runtime\python\python.exe')),'--data-dir',('"{0}"' -f $run),'--cache-data-dir',('"{0}"' -f $cache),'--fixture-directory',('"{0}"' -f (Join-Path $run 'fixtures')),'--check-shortcut-actions','--no-startup')
@{seed=0;native=[IO.Path]::GetFullPath($Native);source=[IO.Path]::GetFullPath($Source);bindingPreset=$bindings;protocol='Real registered Windows shortcut messages and shared command queue; generated frame through native host/backend/overlay';desktopCaptured=$false;physicalKeyboardDelivered=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $run 'run-config.json') -Encoding UTF8
Write-Output 'STEP: real shortcut dispatch, cold/warm toolbox, inference and English pixels'
$process=Start-Process -FilePath (Join-Path $app 'runtime\dotnet\dotnet.exe') -ArgumentList $arguments -WorkingDirectory $component -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'console.log') -RedirectStandardError (Join-Path $run 'console-error.log')
# Retain the native handle before exit so Windows PowerShell can read the exit code.
$null=$process.Handle
if(!$process.WaitForExit(240000)){throw 'Shortcut check timed out; inspect the retained process and logs'}
$process.Refresh()
if($process.ExitCode -ne 0){throw ('Shortcut check failed: '+$process.ExitCode)}
Get-Content -LiteralPath (Join-Path $run 'shortcut-action-check.json')
Write-Output 'PASS: real shortcut dispatch and generated-frame translation checks'

[CmdletBinding()]
param([switch]$Activate,
 [ValidateSet('C:\Users\jlewi\iCloudDrive\[REDACTED]')][string]$AdditionalSourceRoot)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI' -or $env:USERNAME -ne 'jlewi'){throw 'Run on MSI as jlewi, normally, after coordinating the idle worker handoff.'}
$packageRoot=Split-Path -Parent $PSScriptRoot
$installRoot='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media'
$version=Join-Path $installRoot 'v0.4.1'
function Ordinary([string]$p){$walk=[IO.Path]::GetFullPath($p);while($walk){if(Test-Path -LiteralPath $walk){$a=[IO.File]::GetAttributes($walk);if(([int]$a -band (0x400 -bor 0x1000 -bor 0x40000 -bor 0x400000)) -ne 0){throw "Unavailable or linked deployment path: $walk"}};$walk=[IO.Path]::GetDirectoryName($walk)}}
function Hash([string]$p){$h=[Security.Cryptography.SHA256]::Create();$f=[IO.File]::OpenRead($p);try{return [BitConverter]::ToString($h.ComputeHash($f)).Replace('-','').ToLowerInvariant()}finally{$f.Dispose();$h.Dispose()}}
Ordinary $packageRoot;Ordinary $installRoot
$owner=Get-Content -LiteralPath (Join-Path $installRoot 'OWNER.json') -Raw|ConvertFrom-Json
if($owner.format -ne 'buddy-media-install-v1' -or $owner.sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Wrong MSI installation owner.'}
$manifest=Get-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Raw|ConvertFrom-Json
if($manifest.format -ne 'buddy-assistant-package-v1' -or $manifest.version -ne '0.4.1-preview'){throw 'Unknown package.'}
foreach($f in $manifest.files.PSObject.Properties){if($f.Name -match '(^/|\\|(^|/)\.\.(/|$))'){throw 'Invalid package path.'};$p=Join-Path $packageRoot $f.Name;Ordinary $p;if((Hash $p) -ne $f.Value){throw "Package hash mismatch: $($f.Name)"}}
$workers=@(Get-ScheduledTask -ErrorAction Stop|Where-Object {$_.TaskName -like 'ED-IT Buddy Media*' -and $_.State -in @('Running','Queued')})
if($workers.Count){throw 'Existing media task is active. Its owner must finish the coordinated handoff first.'}
$guard=$null
try {
 $lock=Join-Path $installRoot 'state\worker.lock';Ordinary $lock
 if(Test-Path -LiteralPath $lock){$guard=[IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
 $python=Join-Path $installRoot 'runtime\python.exe'
 $state=Join-Path $installRoot 'state'
 $queryCode = @'
import sqlite3,sys
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
print(c.execute('SELECT count(*) FROM jobs WHERE status IN (?,?)',('running','queued')).fetchone()[0])
'@
 $encodedQuery=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($queryCode))
 $unfinished=& $python -I -c "exec(__import__('base64').b64decode('$encodedQuery'))" (Join-Path $state 'media.sqlite3')
 if($LASTEXITCODE -ne 0 -or [int]$unfinished -ne 0){throw 'Unfinished media jobs remain. Do not replace their worker.'}
 Ordinary $version
 $target=Join-Path $version 'media_worker'
 New-Item -ItemType Directory -Path $target -Force|Out-Null
 $hashes=[ordered]@{}
 foreach($f in $manifest.files.PSObject.Properties|Where-Object {$_.Name -like 'media_worker/*'}){
  $name=$f.Name.Substring('media_worker/'.Length);if($name.Contains('/')){throw 'Unexpected nested worker source.'}
  $destination=Join-Path $target $name;Ordinary $destination
  if(Test-Path -LiteralPath $destination){if((Hash $destination) -ne $f.Value){throw 'Immutable worker version already has different bytes.'}}
  else{Copy-Item -LiteralPath (Join-Path $packageRoot $f.Name) -Destination $destination}
  if((Hash $destination) -ne $f.Value){throw 'Deployed worker readback failed.'};$hashes[$name]=$f.Value
 }
 $sealPath=Join-Path $version 'code-hashes.json';Ordinary $sealPath;$sealJson=$hashes|ConvertTo-Json
 if(Test-Path -LiteralPath $sealPath){if((Get-Content -LiteralPath $sealPath -Raw).Trim() -ne $sealJson.Trim()){throw 'Existing immutable code seal differs.'}}else{$sealJson|Set-Content -LiteralPath $sealPath -Encoding UTF8}
 $config=Get-Content -LiteralPath (Join-Path $installRoot 'config.json') -Raw|ConvertFrom-Json
 if($AdditionalSourceRoot){$config.source_allowlist=@(@($config.source_allowlist)+$AdditionalSourceRoot|Select-Object -Unique)}
 $config|Add-Member -Force NoteProperty archive_root 'C:\Users\jlewi\Documents\ED-IT Buddy Media Library'
 $config|Add-Member -Force NoteProperty archive_excluded_roots @('C:\Users\jlewi\AppData\Local\ProjectLibrary\Cache')
 $configPath=Join-Path $version 'config.json'
 $json=$config|ConvertTo-Json -Depth 12
 if(Test-Path -LiteralPath $configPath){if((Get-Content -LiteralPath $configPath -Raw).Trim() -ne $json.Trim()){throw 'Existing version configuration differs; preserve and review it.'}}
 else{$json|Set-Content -LiteralPath $configPath -Encoding UTF8}
 if(!$Activate){Write-Output 'Immutable worker staged. No worker started. Use -Activate only during the coordinated idle window.';return}
 $name='ED-IT Buddy Media v0.4.1';$preflight=$name+' Preflight'
 if((Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) -or (Get-ScheduledTask -TaskName $preflight -ErrorAction SilentlyContinue)){throw 'New task name already exists; inspect its outcome rather than replaying activation.'}
 $worker=Join-Path $target 'worker.py'
 $pythonw=Join-Path $installRoot 'runtime\pythonw.exe';if(!(Test-Path -LiteralPath $pythonw)){throw 'Scheduled interactive pythonw runtime is unavailable.'}
 $arguments='-B -u "'+$worker+'" --state "'+$state+'" --config "'+$configPath+'"'
 $principal=New-ScheduledTaskPrincipal -UserId 'MSI\jlewi' -LogonType Interactive -RunLevel Limited
 $settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
 $action=New-ScheduledTaskAction -Execute $pythonw -Argument ($arguments+' --preflight') -WorkingDirectory $version
 Register-ScheduledTask -TaskName $preflight -Action $action -Principal $principal -Settings $settings|Out-Null
 if($guard){$guard.Dispose();$guard=$null}
 $started=[DateTime]::UtcNow;Start-ScheduledTask -TaskName $preflight
 $deadline=$started.AddMinutes(5)
 do {Start-Sleep -Seconds 2;$task=Get-ScheduledTask -TaskName $preflight;$info=Get-ScheduledTaskInfo -TaskName $preflight;$ran=$info.LastRunTime.ToUniversalTime() -ge $started.AddSeconds(-2)}while((!$ran -or $task.State -in @('Running','Queued')) -and [DateTime]::UtcNow -lt $deadline)
 if(!$ran -or $task.State -in @('Running','Queued') -or $info.LastTaskResult -ne 0){throw 'Scheduled-user GPU preflight failed. Keep evidence and inspect; worker was not started.'}
 $proof=Get-Content -LiteralPath (Join-Path $state 'gpu-preflight.json') -Raw|ConvertFrom-Json
 if(!$proof.passed -or $proof.host -ne 'MSI' -or $proof.model_revision -ne $config.model_revision -or !$proof.runtime.cuda){throw 'GPU receipt differs from this configuration.'}
 foreach($source in @('core.py','jobs.py','vision.py','worker.py','archive.py')){if($proof.source_sha256.$source -ne $hashes[$source]){throw 'GPU receipt does not identify this worker source.'}}
 $action=New-ScheduledTaskAction -Execute $pythonw -Argument $arguments -WorkingDirectory $version
 Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings|Out-Null
 Start-ScheduledTask -TaskName $name
 Write-Output 'v0.4.1 scheduled worker started after matching GPU preflight. Actual transfer/search/delivery acceptance remains required.'
} finally {if($guard){$guard.Dispose()}}

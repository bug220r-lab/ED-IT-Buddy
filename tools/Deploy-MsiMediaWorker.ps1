[CmdletBinding()]
param([string]$Version='v0.3.1',[switch]$RunPreflightAndStart,[switch]$StartOnly,[switch]$ReuseConfiguration)
$ErrorActionPreference='Stop'
if($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9-]+)?$'){throw 'Use a new version identifier.'}
$mediaSourceRoot=Split-Path -Parent $PSScriptRoot
$ssh=Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
$scp=Join-Path $env:WINDIR 'System32\OpenSSH\scp.exe'
$identity='C:\Users\Jude\.ssh\edit_buddy_msi_ed25519'
$sshOptions=@('-i',$identity,'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','HostKeyAlias=msi','-o','ForwardAgent=no','-o','ClearAllForwardings=yes','-o','ConnectTimeout=8')
function Invoke-MediaRemote([string]$Script){
    # Windows OpenSSH eventually invokes cmd.exe, whose command line is bounded.
    # Transfer the exact reviewed payload, then invoke it through a short hash check.
    $id=[guid]::NewGuid().ToString('N')
    $localCommands=Join-Path $mediaSourceRoot '.build\media-commands'
    New-Item -ItemType Directory -Path $localCommands -Force|Out-Null
    $localCommand=Join-Path $localCommands ($id+'.ps1')
    [IO.File]::WriteAllText($localCommand,$Script,(New-Object Text.UTF8Encoding($true)))
    $expected=(Get-FileHash -LiteralPath $localCommand -Algorithm SHA256).Hash.ToLowerInvariant()
    $ensure=@'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI' -or $env:USERNAME -ne 'jlewi'){throw 'Wrong host/account.'}
$root='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media'
$owner=Get-Content -LiteralPath ($root+'\OWNER.json') -Raw|ConvertFrom-Json
if($owner.format -ne 'buddy-media-install-v1' -or $owner.sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Unowned media install.'}
$commands=$root+'\commands'
foreach($p in @($root,$commands)){if(Test-Path -LiteralPath $p){if((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked command staging refused.'}}}
if(!(Test-Path -LiteralPath $commands)){New-Item -ItemType Directory -Path $commands|Out-Null}
'@
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($ensure))
    & $ssh @sshOptions 'jlewi@192.168.247.1' powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded
    if($LASTEXITCODE -ne 0){throw 'MSI command staging could not be verified.'}
    $remoteCommand='C:/Users/jlewi/AppData/Local/ED-IT-Buddy-Media/commands/'+$id+'.ps1'
    & $scp @sshOptions $localCommand ('jlewi@192.168.247.1:'+$remoteCommand)
    if($LASTEXITCODE -ne 0){throw 'Reviewed command transfer failed; no execution attempted.'}
    $invoke="`$ErrorActionPreference='Stop';`$p='"+$remoteCommand+"';if((Get-FileHash -LiteralPath `$p -Algorithm SHA256).Hash.ToLowerInvariant() -ne '"+$expected+"'){throw 'Command hash mismatch.'};& `$p"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($invoke))
    & $ssh @sshOptions 'jlewi@192.168.247.1' powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded
    if($LASTEXITCODE -ne 0){throw 'MSI operation failed. Inspect existing state before retrying.'}
}
$remoteRoot='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media'
$remoteVersion=$remoteRoot+'\'+$Version
if(!$StartOnly){
$prepare=@'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI' -or $env:USERNAME -ne 'jlewi'){throw 'Wrong host/account.'}
$root='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media'
$owner=Get-Content -LiteralPath ($root+'\OWNER.json') -Raw|ConvertFrom-Json
if($owner.format -ne 'buddy-media-install-v1' -or $owner.sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Unowned media install.'}
if(!(Test-Path -LiteralPath ($root+'\runtime\python.exe'))){throw 'Prepare the separate runtime first.'}
$version=Join-Path $root '__VERSION__'
if(Test-Path -LiteralPath $version){throw 'Version exists; choose a fresh version, never overwrite it.'}
New-Item -ItemType Directory -Path $version|Out-Null
'@
Invoke-MediaRemote ($prepare.Replace('__VERSION__',$Version))
$stage=Join-Path $mediaSourceRoot ('.build\media-deploy-'+[guid]::NewGuid().ToString('N'))
$code=Join-Path $stage 'media_worker';New-Item -ItemType Directory -Path $code -Force|Out-Null
$hashes=@{}
foreach($f in Get-ChildItem -LiteralPath (Join-Path $mediaSourceRoot 'media_worker') -File){
    if($f.Extension -in @('.py','.json','.md','.txt')){Copy-Item -LiteralPath $f.FullName -Destination $code;$hashes[$f.Name]=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$hashes|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'code-hashes.json') -Encoding UTF8
& $scp @sshOptions -r $code ('jlewi@192.168.247.1:'+($remoteVersion.Replace('\','/'))+'/')
if($LASTEXITCODE -ne 0){throw 'Code transfer failed; incomplete version retained. Choose a new version after inspection.'}
& $scp @sshOptions (Join-Path $stage 'code-hashes.json') ('jlewi@192.168.247.1:'+($remoteVersion.Replace('\','/'))+'/code-hashes.json')
if($LASTEXITCODE -ne 0){throw 'Manifest transfer failed.'}
$finish=@'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$root='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media';$version=Join-Path $root '__VERSION__'
$manifest=Get-Content -LiteralPath ($version+'\code-hashes.json') -Raw|ConvertFrom-Json
foreach($p in $manifest.PSObject.Properties){$f=Join-Path ($version+'\media_worker') $p.Name;if((Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p.Value){throw 'Deployed source hash mismatch.'}}
$pin=Get-Content -LiteralPath ($root+'\model-lock.json') -Raw|ConvertFrom-Json
if($pin.model -ne 'google/siglip2-base-patch16-224' -or $pin.revision -notmatch '^[a-f0-9]{40}$'){throw 'Model pin missing.'}
$config=Join-Path $root 'config.json'
$expectedConfig=@{expected_host='MSI';downloader_root='C:\Users\jlewi\Documents\VideoDownloader';source_allowlist=@('C:\Users\jlewi\Documents','C:\Users\jlewi\OneDrive\Documents');model_path=$pin.local_path;model_revision=$pin.revision}
$reuse='__REUSE_CONFIGURATION__' -eq 'true'
if(Test-Path -LiteralPath $config){
    if(!$reuse){throw 'Configuration already exists; use reviewed -ReuseConfiguration for a fresh code version.'}
    if((Get-Item -LiteralPath $config -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked configuration refused.'}
    $existing=Get-Content -LiteralPath $config -Raw|ConvertFrom-Json
    if((@($existing.PSObject.Properties.Name|Sort-Object)-join '|') -ne (@($expectedConfig.Keys|Sort-Object)-join '|')){throw 'Existing configuration fields differ from reviewed configuration.'}
    foreach($key in @('expected_host','downloader_root','model_path','model_revision')){if($existing.$key -cne $expectedConfig[$key]){throw ('Existing configuration differs: '+$key)}}
    if((@($existing.source_allowlist)-join '|') -cne (@($expectedConfig.source_allowlist)-join '|')){throw 'Existing source allowlist differs from reviewed configuration.'}
    Write-Output 'Existing owned configuration matches reviewed values; left unchanged.'
}else{
    if($reuse){throw 'Expected existing configuration is missing; no implicit replacement.'}
    $expectedConfig|ConvertTo-Json|Set-Content -LiteralPath $config -Encoding UTF8
}
Write-Output ('Verified code prepared at '+$version+'; worker is not started.')
'@
Invoke-MediaRemote ($finish.Replace('__VERSION__',$Version).Replace('__REUSE_CONFIGURATION__',([bool]$ReuseConfiguration).ToString().ToLowerInvariant()))
}
if($RunPreflightAndStart -or $StartOnly){
    $start=@'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$root='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media';$version=Join-Path $root '__VERSION__';$python=$root+'\runtime\python.exe';$worker=$version+'\media_worker\worker.py'
$owner=Get-Content -LiteralPath ($root+'\OWNER.json') -Raw|ConvertFrom-Json
if($env:COMPUTERNAME -ne 'MSI' -or $env:USERNAME -ne 'jlewi' -or $owner.format -ne 'buddy-media-install-v1' -or $owner.sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Wrong MSI installation owner.'}
$manifest=Get-Content -LiteralPath ($version+'\code-hashes.json') -Raw|ConvertFrom-Json
foreach($p in $manifest.PSObject.Properties){$f=Join-Path ($version+'\media_worker') $p.Name;if((Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant() -ne $p.Value){throw 'Existing deployed source hash mismatch.'}}
# pythonw avoids opening a console on the editing user's desktop. The worker
# persists both preflight evidence and startup errors under its owned state.
if(Test-Path -LiteralPath ($root+'\runtime\pythonw.exe')){$python=$root+'\runtime\pythonw.exe'}
$name='ED-IT Buddy Media __VERSION__';$preflightName=$name+' Preflight'
if((Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) -or (Get-ScheduledTask -TaskName $preflightName -ErrorAction SilentlyContinue)){throw 'Task name exists; inspect before retry.'}
$principal=New-ScheduledTaskPrincipal -UserId 'MSI\jlewi' -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$args='-u "'+$worker+'" --state "'+$root+'\state" --config "'+$root+'\config.json"'
$pre=New-ScheduledTaskAction -Execute $python -Argument ($args+' --preflight') -WorkingDirectory $version
Register-ScheduledTask -TaskName $preflightName -Action $pre -Principal $principal -Settings $settings -Description 'Buddy media GPU preflight only; no MAGIX input or source edits.'|Out-Null
$started=[DateTime]::UtcNow
Start-ScheduledTask -TaskName $preflightName
$deadline=[DateTime]::UtcNow.AddMinutes(5)
do{
    Start-Sleep -Seconds 2
    $task=Get-ScheduledTask -TaskName $preflightName
    $info=Get-ScheduledTaskInfo -TaskName $preflightName
    $ran=$info.LastRunTime.ToUniversalTime() -ge $started.AddSeconds(-2)
}while((!$ran -or $task.State -eq 'Running' -or $task.State -eq 'Queued') -and [DateTime]::UtcNow -lt $deadline)
if(!$ran -or $task.State -in @('Running','Queued') -or $info.LastTaskResult -ne 0){throw 'GPU preflight did not pass; inspect state\last-worker-error.json and retain task/state before retry.'}
$proof=Get-Content -LiteralPath ($root+'\state\gpu-preflight.json') -Raw|ConvertFrom-Json
$pin=Get-Content -LiteralPath ($root+'\model-lock.json') -Raw|ConvertFrom-Json
$observed=[DateTimeOffset]::FromUnixTimeMilliseconds([long]([double]$proof.observed_at*1000)).UtcDateTime
if(!$proof.passed -or $proof.host -ne 'MSI' -or $proof.model_revision -ne $pin.revision -or $observed -lt $started.AddSeconds(-2) -or !$proof.runtime.cuda){throw 'GPU receipt does not establish a fresh matching MSI CUDA preflight.'}
$hashes=Get-Content -LiteralPath ($version+'\code-hashes.json') -Raw|ConvertFrom-Json
foreach($source in @('core.py','jobs.py','vision.py','worker.py')){if($proof.source_sha256.$source -ne $hashes.$source){throw 'GPU receipt does not match deployed source.'}}
$action=New-ScheduledTaskAction -Execute $python -Argument $args -WorkingDirectory $version
Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Description 'Owned Buddy media jobs; independent of SSH and MAGIX.'|Out-Null
Start-ScheduledTask -TaskName $name
Write-Output 'GPU preflight passed; scheduled media worker started. No footage indexed automatically.'
'@
    Invoke-MediaRemote ($start.Replace('__VERSION__',$Version))
}
Write-Output 'Deployment receipt: source hashes retained in the new MSI version directory. Review assistant/config.json before connecting.'

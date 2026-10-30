[CmdletBinding()]
param([string]$Version='v0.3.0',[switch]$RunPreflightAndStart)
$ErrorActionPreference='Stop'
if($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9-]+)?$'){throw 'Use a new version identifier.'}
$mediaSourceRoot=Split-Path -Parent $PSScriptRoot
$ssh=Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
$scp=Join-Path $env:WINDIR 'System32\OpenSSH\scp.exe'
$identity='C:\Users\Jude\.ssh\edit_buddy_msi_ed25519'
$sshOptions=@('-i',$identity,'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','HostKeyAlias=msi','-o','ForwardAgent=no','-o','ClearAllForwardings=yes','-o','ConnectTimeout=8')
function Invoke-MediaRemote([string]$Script){
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    & $ssh @sshOptions 'jlewi@192.168.247.1' powershell.exe -NoProfile -NonInteractive -EncodedCommand $encoded
    if($LASTEXITCODE -ne 0){throw 'MSI operation failed. Inspect existing state before retrying.'}
}
$remoteRoot='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media'
$remoteVersion=$remoteRoot+'\'+$Version
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
if(Test-Path -LiteralPath $config){throw 'Configuration already exists; review it rather than overwrite.'}
@{expected_host='MSI';downloader_root='C:\Users\jlewi\Documents\VideoDownloader';source_allowlist=@('C:\Users\jlewi\Documents','C:\Users\jlewi\OneDrive\Documents');model_path=$pin.local_path;model_revision=$pin.revision}|ConvertTo-Json|Set-Content -LiteralPath $config -Encoding UTF8
Write-Output ('Verified code prepared at '+$version+'; worker is not started.')
'@
Invoke-MediaRemote ($finish.Replace('__VERSION__',$Version))
if($RunPreflightAndStart){
    $start=@'
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$root='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media';$version=Join-Path $root '__VERSION__';$python=$root+'\runtime\python.exe';$worker=$version+'\media_worker\worker.py'
$name='ED-IT Buddy Media __VERSION__';$preflightName=$name+' Preflight'
if((Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) -or (Get-ScheduledTask -TaskName $preflightName -ErrorAction SilentlyContinue)){throw 'Task name exists; inspect before retry.'}
$principal=New-ScheduledTaskPrincipal -UserId 'MSI\jlewi' -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$args='-u "'+$worker+'" --state "'+$root+'\state" --config "'+$root+'\config.json"'
$pre=New-ScheduledTaskAction -Execute $python -Argument ($args+' --preflight') -WorkingDirectory $version
Register-ScheduledTask -TaskName $preflightName -Action $pre -Principal $principal -Settings $settings -Description 'Buddy media GPU preflight only; no MAGIX input or source edits.'|Out-Null
Start-ScheduledTask -TaskName $preflightName
$deadline=[DateTime]::UtcNow.AddMinutes(5)
do{Start-Sleep -Seconds 2;$task=Get-ScheduledTask -TaskName $preflightName;$info=Get-ScheduledTaskInfo -TaskName $preflightName}while($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline)
if($task.State -eq 'Running' -or $info.LastTaskResult -ne 0){throw 'GPU preflight did not pass; retain task/state for inspection.'}
$proof=Get-Content -LiteralPath ($root+'\state\gpu-preflight.json') -Raw|ConvertFrom-Json
if(!$proof.passed){throw 'GPU receipt does not establish success.'}
$action=New-ScheduledTaskAction -Execute $python -Argument $args -WorkingDirectory $version
Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Description 'Owned Buddy media jobs; independent of SSH and MAGIX.'|Out-Null
Start-ScheduledTask -TaskName $name
Write-Output 'GPU preflight passed; scheduled media worker started. No footage indexed automatically.'
'@
    Invoke-MediaRemote ($start.Replace('__VERSION__',$Version))
}
Write-Output 'Deployment receipt: source hashes retained in the new MSI version directory. Review assistant/config.json before connecting.'

$ErrorActionPreference='Stop'
$portalCloseBody=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal'
$receipt=Join-Path $root 'state\host-close-result.json'
try {
 if($env:COMPUTERNAME -ne 'MSI' -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'Requires MSI interactive session.'}
 $endpoint=Get-Content -LiteralPath (Join-Path $root 'state\endpoint.json') -Raw|ConvertFrom-Json
 Get-ChildItem -LiteralPath (Join-Path $root 'state\jobs') -Filter '*.json' -File -ErrorAction SilentlyContinue|ForEach-Object {$job=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json;if($job.status -in @('running','queued')){throw 'An editing sequence is active.'}}
 $p=Get-Process -Id $endpoint.process_id -ErrorAction SilentlyContinue
 if($p){
  if($p.Path -ine (Join-Path $root 'v0.2.0\ED-IT Buddy.Portal.exe') -or $p.StartTime.ToUniversalTime().Ticks -ne $endpoint.process_start_ticks){throw 'Host identity changed.'}
  if(!$p.CloseMainWindow()){throw 'The host did not accept a normal close.'}
  if(!$p.WaitForExit(10000)){throw 'The host did not exit; no force termination was used.'}
 }
 @{ok=$true;nonce='__NONCE__'}|ConvertTo-Json -Compress|Set-Content -LiteralPath $receipt -Encoding UTF8
}catch{@{ok=$false;nonce='__NONCE__';error=$_.Exception.Message}|ConvertTo-Json -Compress|Set-Content -LiteralPath $receipt -Encoding UTF8;exit 1}
'@
$portalCloseNonce=[Guid]::NewGuid().ToString('N')
$portalCloseBody=$portalCloseBody.Replace('__NONCE__',$portalCloseNonce)
$portalCloseFile=Join-Path (Split-Path -Parent $PSScriptRoot) '.build\portal-live\close-host-task.ps1'
[IO.File]::WriteAllText($portalCloseFile,$portalCloseBody,[Text.UTF8Encoding]::new($false))
& 'C:\Windows\System32\OpenSSH\scp.exe' -i 'C:\Users\Jude\.ssh\edit_buddy_msi_ed25519' -o 'UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts' -o HostKeyAlias=msi -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes $portalCloseFile 'jlewi@192.168.247.1:C:/Users/jlewi/AppData/Local/ED-IT Buddy/portal/probe/close-host-task.ps1'
if($LASTEXITCODE -ne 0){throw 'Could not stage host maintenance task.'}
$portalCloseSetup=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI'){throw 'Wrong host.'}
$name='ED-IT Buddy Portal Maintenance 020'
$old=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
if($old -and ($old.Description -ne 'ED-IT Buddy controlled host update' -or $old.State -eq 'Running')){throw 'Maintenance task unavailable.'}
$script=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\probe\close-host-task.ps1'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File "'+$script+'"')
$principal=New-ScheduledTaskPrincipal -UserId 'MSI\jlewi' -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Description 'ED-IT Buddy controlled host update' -Force|Out-Null
Start-ScheduledTask -TaskName $name
$receipt=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\state\host-close-result.json'
for($i=0;$i -lt 20;$i++){
 Start-Sleep -Seconds 1
 if(Test-Path -LiteralPath $receipt){$r=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json;if($r.nonce -eq '__NONCE__'){$r|ConvertTo-Json -Compress;if(!$r.ok){exit 1};exit 0}}
}
throw 'The desktop maintenance task did not return a receipt.'
'@
$portalCloseSetup=$portalCloseSetup.Replace('__NONCE__',$portalCloseNonce)
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalCloseSetup))
& 'C:\Windows\System32\OpenSSH\ssh.exe' -i 'C:\Users\Jude\.ssh\edit_buddy_msi_ed25519' -o 'UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts' -o HostKeyAlias=msi -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'Buddy host was not closed for updating.'}

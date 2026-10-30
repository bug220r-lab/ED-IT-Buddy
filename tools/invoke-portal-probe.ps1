[CmdletBinding()]
param([ValidatePattern('^[A-Za-z0-9_-]+\.json$')][string]$RequestFile,[switch]$LaunchMagix,[switch]$Capture)
$ErrorActionPreference='Stop'
$portalRunId=[Guid]::NewGuid().ToString('N')
if($Capture -and $RequestFile){throw 'Capture and an editing request cannot run together.'}
$portalProbeCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI'){throw 'Wrong host.'}
$name='ED-IT Buddy Portal Probe 020'
$script=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\probe\portal-interactive-probe.ps1'
$task=Get-ScheduledTask -TaskName $name
if($task.State -eq 'Running'){throw 'The previous probe is still running.'}
if($task.Description -ne 'ED-IT Buddy approved disposable MAGIX portal readiness probe'){throw 'Task ownership changed.'}
$args='-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File "'+$script+'" -RunId __RUNID__ __ARGS__'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $args
Set-ScheduledTask -TaskName $name -Action $action|Out-Null
[ordered]@{ok=$false;status='scheduled';run_id='__RUNID__';started_utc=[DateTime]::UtcNow.ToString('o');request='__REQUEST__'}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path (Split-Path -Parent $script) 'latest.json') -Encoding UTF8
Start-ScheduledTask -TaskName $name
[ordered]@{run_id='__RUNID__';started_utc=[DateTime]::UtcNow.ToString('o');request='__REQUEST__'}|ConvertTo-Json -Compress
'@
$portalProbeArgs=if($RequestFile){'-RequestFile '+$RequestFile}elseif($Capture){'-Capture'}elseif($LaunchMagix){'-LaunchMagix'}else{''}
$portalProbeCode=$portalProbeCode.Replace('__ARGS__',$portalProbeArgs).Replace('__REQUEST__',$RequestFile).Replace('__RUNID__',$portalRunId)
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalProbeCode))
$portalInvocation=& 'C:\Windows\System32\OpenSSH\ssh.exe' -i 'C:\Users\Jude\.ssh\edit_buddy_msi_ed25519' -o 'UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts' -o HostKeyAlias=msi -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'The interactive probe could not be started.'}
$portalInvocationText=$portalInvocation -join "`n"
$portalInvocationObject=$portalInvocationText|ConvertFrom-Json
if($portalInvocationObject.run_id -ne $portalRunId){throw 'Probe invocation identity mismatch.'}
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $PSScriptRoot) '.build\portal-live\latest-probe-invocation.json'),$portalInvocationText,[Text.UTF8Encoding]::new($false))
Write-Output $portalInvocationText

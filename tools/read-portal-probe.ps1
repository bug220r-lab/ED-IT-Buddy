[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
$portalWorkspace=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$portalOutput=[IO.Path]::GetFullPath((Join-Path $portalWorkspace $OutputFile))
if(!$portalOutput.StartsWith($portalWorkspace+'\.build\portal-live\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use .build/portal-live for diagnostics.'}
if(Test-Path -LiteralPath $portalOutput){throw 'Diagnostic output already exists.'}
$portalInvocation=Get-Content -LiteralPath (Join-Path $portalWorkspace '.build\portal-live\latest-probe-invocation.json') -Raw|ConvertFrom-Json
if($portalInvocation.run_id -notmatch '^[a-f0-9]{32}$'){throw 'Invalid expected probe identity.'}
$portalReadCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$task=Get-ScheduledTask -TaskName 'ED-IT Buddy Portal Probe 020'
if($task.State -eq 'Running'){throw 'The probe is still running.'}
$root=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\probe'
$receipt=Get-Content -LiteralPath (Join-Path $root 'latest.json') -Raw|ConvertFrom-Json
if($receipt.run_id -ne '__RUNID__'){throw 'Stale probe receipt: it does not belong to the latest requested run.'}
if(!$receipt.ok){throw $receipt.error}
$snapshot=[IO.Path]::GetFullPath($receipt.snapshot)
if(!$snapshot.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid snapshot path.'}
if($receipt.capture){$receipt|ConvertTo-Json -Compress}else{Get-Content -LiteralPath $snapshot -Raw}
'@
$portalReadCode=$portalReadCode.Replace('__RUNID__',[string]$portalInvocation.run_id)
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalReadCode))
$result=& 'C:\Windows\System32\OpenSSH\ssh.exe' -i 'C:\Users\Jude\.ssh\edit_buddy_msi_ed25519' -o 'UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts' -o HostKeyAlias=msi -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'Could not read probe.'}
$null=($result -join "`n")|ConvertFrom-Json
[IO.File]::WriteAllText($portalOutput,($result -join "`n"),[Text.UTF8Encoding]::new($false))
Write-Output $portalOutput

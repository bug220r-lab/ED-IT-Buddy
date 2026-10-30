[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$portalWorkspace=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$portalBuild=[IO.Path]::GetFullPath((Join-Path $portalWorkspace $BuildDirectory))
if(!$portalBuild.StartsWith($portalWorkspace+'\.build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use a versioned .build output.'}
$portalFiles=@('ED-IT Buddy.Portal.exe','ED-IT Buddy.Portal.exe.config','ED-IT Buddy.Bridge.exe','ED-IT Buddy.Bridge.exe.config','ED-IT Buddy.EditorWorker.exe','ED-IT Buddy.EditorWorker.exe.config','manifest.json')
foreach($name in $portalFiles){if(!(Test-Path -LiteralPath (Join-Path $portalBuild $name))){throw ('Missing '+$name)}}
$portalOptions=@('-i','C:\Users\Jude\.ssh\edit_buddy_msi_ed25519','-o','UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts','-o','HostKeyAlias=msi','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=10')
$portalStopCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI'){throw 'Wrong host.'}
$root=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal'
$endpoint=Get-Content -LiteralPath (Join-Path $root 'state\endpoint.json') -Raw|ConvertFrom-Json
$expected=Join-Path $root 'v0.2.0\ED-IT Buddy.Portal.exe'
Get-ChildItem -LiteralPath (Join-Path $root 'state\jobs') -Filter '*.json' -File -ErrorAction SilentlyContinue|ForEach-Object {
 $job=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json
 if($job.status -in @('running','queued')){throw 'A sequence is still active. Stop it before updating Buddy.'}
}
$process=Get-Process -Id $endpoint.process_id -ErrorAction SilentlyContinue
if($process){
 if($process.Path -ine $expected -or $process.StartTime.ToUniversalTime().Ticks -ne $endpoint.process_start_ticks){throw 'Host process identity changed.'}
 if(!$process.CloseMainWindow()){throw 'Buddy did not accept a normal close request.'}
 if(!$process.WaitForExit(10000)){throw 'Buddy did not exit. It was not force-terminated.'}
}
# Older bridge builds could remain blocked on SSH stdin after their host exited.
# Only this deployment's session-zero relays are disposable; MAGIX is unrelated.
$bridgePath=Join-Path $root 'v0.2.0\ED-IT Buddy.Bridge.exe'
Get-Process -Name 'ED-IT Buddy.Bridge' -ErrorAction SilentlyContinue|ForEach-Object {
 if($_.SessionId -eq 0 -and $_.Path -ieq $bridgePath){Stop-Process -InputObject $_ -ErrorAction Stop}
}
$task=Get-ScheduledTask -TaskName 'ED-IT Buddy Portal v0.2.0'
if($task.Actions.Execute -ine $expected -or $task.Principal.RunLevel -ne 'Limited'){throw 'The dedicated task no longer matches.'}
'Owned idle Buddy host closed. MAGIX was not closed.'
'@
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalStopCode))
& 'C:\Windows\System32\OpenSSH\ssh.exe' @portalOptions 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'Host update preflight failed.'}
$portalPaths=@($portalFiles|ForEach-Object {Join-Path $portalBuild $_})
& 'C:\Windows\System32\OpenSSH\scp.exe' @portalOptions @portalPaths 'jlewi@192.168.247.1:C:/Users/jlewi/AppData/Local/ED-IT Buddy/portal/v0.2.0/'
if($LASTEXITCODE -ne 0){throw 'File transfer failed; host remains stopped.'}
$portalStartCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$target=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\v0.2.0'
$manifest=Get-Content -LiteralPath (Join-Path $target 'manifest.json') -Raw|ConvertFrom-Json
$expectedFiles=@('ED-IT Buddy.Portal.exe','ED-IT Buddy.Portal.exe.config','ED-IT Buddy.Bridge.exe','ED-IT Buddy.Bridge.exe.config','ED-IT Buddy.EditorWorker.exe','ED-IT Buddy.EditorWorker.exe.config')
if(@($manifest).Count -ne 6 -or @($manifest.file|Sort-Object -Unique).Count -ne 6){throw 'Manifest must contain six distinct runtime files.'}
foreach($entry in $manifest){
 if($entry.file -notin $expectedFiles -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'Unexpected package member or invalid hash.'}
 if((Get-FileHash -LiteralPath (Join-Path $target $entry.file) -Algorithm SHA256).Hash -ine $entry.sha256){throw 'Transfer hash mismatch.'}
}
Start-ScheduledTask -TaskName 'ED-IT Buddy Portal v0.2.0'
'Updated Buddy host started after verifying all runtime hashes.'
'@
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalStartCode))
& 'C:\Windows\System32\OpenSSH\ssh.exe' @portalOptions 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'Updated host verification/start failed.'}

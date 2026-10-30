[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory,[string]$Version='v0.2.0')
$ErrorActionPreference='Stop'
if($Version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9]+)?$'){throw 'Invalid deployment version.'}
$portalWorkspace=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$portalBuild=[IO.Path]::GetFullPath((Join-Path $portalWorkspace $BuildDirectory))
if(!$portalBuild.StartsWith($portalWorkspace+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build must be in this workspace.'}
$portalNames=@('ED-IT Buddy.Portal.exe','ED-IT Buddy.Portal.exe.config','ED-IT Buddy.Bridge.exe','ED-IT Buddy.Bridge.exe.config','ED-IT Buddy.EditorWorker.exe','ED-IT Buddy.EditorWorker.exe.config','manifest.json')
foreach($name in $portalNames){if(!(Test-Path -LiteralPath (Join-Path $portalBuild $name) -PathType Leaf)){throw ('Missing artifact: '+$name)}}
$portalSsh='C:\Windows\System32\OpenSSH\ssh.exe'
$portalScp='C:\Windows\System32\OpenSSH\scp.exe'
$portalSshOptions=@('-i','C:\Users\Jude\.ssh\edit_buddy_msi_ed25519','-o','UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts','-o','HostKeyAlias=msi','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=10')
$portalDeployCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI' -or [Security.Principal.WindowsIdentity]::GetCurrent().Name -ne 'MSI\jlewi'){throw 'Unexpected deployment host/account.'}
$root=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal'
$target=Join-Path $root '__VERSION__'
if(Test-Path -LiteralPath $target){throw 'Version folder already exists; deploy a new version.'}
New-Item -ItemType Directory -Path $target -Force|Out-Null
New-Item -ItemType Directory -Path (Join-Path $root 'state') -Force|Out-Null
New-Item -ItemType Directory -Path (Join-Path $root 'workspaces') -Force|Out-Null
$target
'@
$portalDeployCode=$portalDeployCode.Replace('__VERSION__',$Version)
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalDeployCode))
& $portalSsh @portalSshOptions 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'Remote staging failed.'}
$portalFiles=@($portalNames|ForEach-Object {Join-Path $portalBuild $_})
& $portalScp @portalSshOptions @portalFiles ('jlewi@192.168.247.1:C:/Users/jlewi/AppData/Local/ED-IT Buddy/portal/'+$Version+'/')
if($LASTEXITCODE -ne 0){throw 'Artifact transfer failed.'}
$portalVerifyCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$root=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal'
$target=Join-Path $root '__VERSION__'
$manifest=Get-Content -LiteralPath (Join-Path $target 'manifest.json') -Raw|ConvertFrom-Json
$expectedFiles=@('ED-IT Buddy.Portal.exe','ED-IT Buddy.Portal.exe.config','ED-IT Buddy.Bridge.exe','ED-IT Buddy.Bridge.exe.config','ED-IT Buddy.EditorWorker.exe','ED-IT Buddy.EditorWorker.exe.config')
if(@($manifest).Count -ne 6 -or @($manifest.file|Sort-Object -Unique).Count -ne 6){throw 'Manifest must contain six distinct runtime files.'}
foreach($entry in $manifest){
 if($entry.file -notin $expectedFiles -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$'){throw 'Unexpected package member or invalid hash.'}
 $actual=(Get-FileHash -LiteralPath (Join-Path $target $entry.file) -Algorithm SHA256).Hash
 if($actual -ine $entry.sha256){throw ('Transfer hash mismatch: '+$entry.file)}
}
$taskName='ED-IT Buddy Portal __VERSION__'
if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'Deployment task already exists.'}
$hostExe=Join-Path $target 'ED-IT Buddy.Portal.exe'
$action=New-ScheduledTaskAction -Execute $hostExe -Argument '--host' -WorkingDirectory $target
$principal=New-ScheduledTaskPrincipal -UserId 'MSI\jlewi' -LogonType Interactive -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Description 'ED-IT Buddy portal desktop host; on-demand, no password, no elevation'|Out-Null
Start-ScheduledTask -TaskName $taskName
[ordered]@{version='__VERSION__';target=$target;task=$taskName;hashes_verified=$manifest.Count;started_utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress
'@
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalVerifyCode.Replace('__VERSION__',$Version)))
& $portalSsh @portalSshOptions 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded
if($LASTEXITCODE -ne 0){throw 'Remote verification or host registration failed.'}

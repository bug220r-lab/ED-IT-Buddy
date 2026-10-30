[CmdletBinding()]
param(
    [ValidatePattern('^demo-[A-Za-z0-9_-]+$')][string]$WorkspaceName='demo-20261030',
    [ValidatePattern('^[A-Za-z0-9 _-]+$')][string]$ProjectName='Buddy Portal Demo',
    [ValidatePattern('^[A-Za-z0-9 _-]+$')][string]$VideoName='Buddy Portal Demo',
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$VideoOnly
)
$ErrorActionPreference='Stop'
$portalRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$portalOutput=[IO.Path]::GetFullPath((Join-Path $portalRoot $OutputDirectory))
if(!$portalOutput.StartsWith($portalRoot+'\artifacts\',[StringComparison]::OrdinalIgnoreCase)){throw 'Collected results must use a new artifacts subfolder.'}
if(Test-Path -LiteralPath $portalOutput){throw 'Result directory already exists; nothing was overwritten.'}
$portalOptions=@('-i','C:\Users\Jude\.ssh\edit_buddy_msi_ed25519','-o','UserKnownHostsFile=C:/Users/Jude/.ssh/known_hosts','-o','HostKeyAlias=msi','-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=10')
$portalCode=@'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI'){throw 'Wrong host.'}
$root=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\workspaces\__WORKSPACE__'))
$files=if('__VIDEO_ONLY__' -eq 'True'){@('__VIDEO__.mp4')}else{@('__PROJECT__.MVP','__VIDEO__.mp4')}
$result=@(foreach($name in $files){
 $path=Join-Path $root $name
 $item=Get-Item -LiteralPath $path
 if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected result file type.'}
 $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try{$hash=[Security.Cryptography.SHA256]::Create(); try{$sha=([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose()}}finally{$stream.Dispose()}
 [ordered]@{file=$name;bytes=$item.Length;sha256=$sha;last_write_utc=$item.LastWriteTimeUtc.ToString('o')}
})
[ordered]@{host='MSI';workspace=$root;files=$result}|ConvertTo-Json -Depth 4 -Compress
'@
$portalCode=$portalCode.Replace('__WORKSPACE__',$WorkspaceName).Replace('__PROJECT__',$ProjectName).Replace('__VIDEO__',$VideoName).Replace('__VIDEO_ONLY__',([bool]$VideoOnly).ToString())
$portalEncoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($portalCode))
function Read-PortalExportManifest {
 $portalRaw=& 'C:\Windows\System32\OpenSSH\ssh.exe' @portalOptions 'jlewi@192.168.247.1' powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $portalEncoded
 if($LASTEXITCODE -ne 0){throw 'Remote result inspection failed. Export may still be writing.'}
 return (($portalRaw -join "`n")|ConvertFrom-Json)
}
$portalBefore=Read-PortalExportManifest
New-Item -ItemType Directory -Path $portalOutput|Out-Null
foreach($portalFile in $portalBefore.files){
 $portalRemote='jlewi@192.168.247.1:'+($portalBefore.workspace.Replace('\','/'))+'/'+$portalFile.file
 & 'C:\Windows\System32\OpenSSH\scp.exe' @portalOptions $portalRemote (Join-Path $portalOutput $portalFile.file)
 if($LASTEXITCODE -ne 0){throw 'Result copy failed; partial files retained for inspection.'}
 $portalLocal=Get-Item -LiteralPath (Join-Path $portalOutput $portalFile.file)
 if($portalLocal.Length -ne $portalFile.bytes -or (Get-FileHash -LiteralPath $portalLocal.FullName -Algorithm SHA256).Hash -ine $portalFile.sha256){throw 'Copied result hash mismatch.'}
}
$portalAfter=Read-PortalExportManifest
if(($portalBefore|ConvertTo-Json -Depth 5 -Compress) -ne ($portalAfter|ConvertTo-Json -Depth 5 -Compress)){throw 'Remote result changed during transfer; no verification is claimed.'}
[ordered]@{copied_utc=[DateTime]::UtcNow.ToString('o');transfer_verified=$true;content_verified=$false;source=$portalAfter}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $portalOutput 'transfer.json') -Encoding UTF8
Write-Output ('Requested results copied with matching SHA-256 hashes: '+$portalOutput)

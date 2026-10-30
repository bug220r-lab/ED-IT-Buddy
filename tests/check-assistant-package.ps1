[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)

$ErrorActionPreference='Stop'
$packageRoot=[IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\','/')
if(!(Test-Path -LiteralPath $packageRoot -PathType Container)){throw 'Assistant package folder is missing.'}
$expected=@(
 'bin/ED-IT Buddy.exe','bin/ED-IT Buddy.Probe.exe','bin/ED-IT Buddy.exe.config','bin/ED-IT Buddy.Probe.exe.config',
 'assistant/client.mjs','assistant/state.mjs','assistant/remote.mjs','assistant/interlinked.mjs','assistant/codex.mjs','assistant/package.json','assistant/config.example.json',
 'data/review-notes.seed.json','data/review-notes.schema.json',
 'docs/assistant-release.md','docs/interlinked-integration.md','docs/video-downloader-discovery.md',
 'media_worker/__init__.py','media_worker/core.py','media_worker/jobs.py','media_worker/vision.py','media_worker/rpc.py','media_worker/worker.py','media_worker/config.example.json','media_worker/README.md','media_worker/runtime-installation.json','media_worker/requirements.installation.txt',
 'README.md','PREVIEW.txt','package-manifest.json'
)
$all=@(Get-ChildItem -LiteralPath $packageRoot -Recurse -Force)
foreach($item in $all){if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Assistant packages must not contain filesystem links.'}}
$actual=@($all|Where-Object {!$_.PSIsContainer}|ForEach-Object {$_.FullName.Substring($packageRoot.Length+1).Replace('\','/')})
if(@(Compare-Object ($expected|Sort-Object) ($actual|Sort-Object)).Count){throw 'Assistant package is incomplete or contains unexpected files.'}
$manifestFile=Join-Path $packageRoot 'package-manifest.json'
if((Get-Item -LiteralPath $manifestFile).Length -gt 65536){throw 'Assistant package manifest exceeds its bound.'}
$manifest=Get-Content -LiteralPath $manifestFile -Raw|ConvertFrom-Json
if($manifest.format -ne 'buddy-assistant-package-v1' -or $manifest.version -ne '0.3.1-preview' -or $manifest.release_verified -ne $false){throw 'Unexpected assistant package version or release claim.'}
if(@($manifest.files.PSObject.Properties).Count -ne $expected.Count-1){throw 'Assistant hash manifest is incomplete.'}
foreach($entry in $manifest.files.PSObject.Properties){
 if($entry.Name -notin $expected -or $entry.Name -eq 'package-manifest.json' -or $entry.Value -notmatch '^[a-f0-9]{64}$'){throw 'Invalid assistant hash-manifest entry.'}
 $hash=(Get-FileHash -LiteralPath (Join-Path $packageRoot $entry.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
 if($hash -ne $entry.Value){throw ('Assistant package hash mismatch: '+$entry.Name)}
}
foreach($name in @('ED-IT Buddy.exe','ED-IT Buddy.Probe.exe')){
 $executable=Join-Path $packageRoot ('bin\'+$name)
 $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
 if($version.FileVersion -ne '0.3.1.0' -or $version.ProductVersion -ne '0.3.1-preview'){throw ('Assistant executable version is not stamped: '+$name)}
 $bytes=[IO.File]::ReadAllBytes($executable)
 if($bytes.Length -lt 128 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90){throw 'Invalid assistant PE executable.'}
 $offset=[BitConverter]::ToInt32($bytes,60)
 if($offset -lt 0 -or $offset+6 -gt $bytes.Length -or [BitConverter]::ToUInt16($bytes,$offset+4) -ne 0x8664){throw 'Assistant executable must be x64.'}
 [xml]$runtime=Get-Content -LiteralPath ($executable+'.config') -Raw
 if($runtime.configuration.startup.supportedRuntime.sku -ne '.NETFramework,Version=v4.8'){throw 'Unexpected assistant runtime configuration.'}
}
$broker=Get-Content -LiteralPath (Join-Path $packageRoot 'assistant/package.json') -Raw|ConvertFrom-Json
if($broker.version -ne '0.3.1'){throw 'Assistant broker version does not match the desktop package.'}
$connection=Get-Content -LiteralPath (Join-Path $packageRoot 'assistant/config.example.json') -Raw|ConvertFrom-Json
if($connection.remote.rpc -notmatch '\\v0\.3\.2\\media_worker\\rpc\.py$'){throw 'Assistant package does not target the accepted MSI worker version.'}
Write-Output 'PASS: assistant runtime allowlist, complete SHA-256 manifest,0.3.1 executable/broker metadata, x64/.NET4.8, and MSI worker target. No application was launched.'

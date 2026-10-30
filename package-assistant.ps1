[CmdletBinding()]
param([string]$PackageName=('ED-IT-Buddy-0.4.1-preview-'+(Get-Date -Format 'yyyyMMdd-HHmmss')))
$ErrorActionPreference='Stop'
if($PackageName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,90}$' -or $PackageName.Contains('..')){throw 'Invalid package name.'}
$assistantPackageRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$stage=Join-Path $assistantPackageRoot ('.build\assistant-package-'+[Guid]::NewGuid().ToString('N'))
$output=Join-Path $assistantPackageRoot ('dist\'+$PackageName)
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output+'.zip'))){throw 'Package already exists; choose a new name.'}
New-Item -ItemType Directory -Path $stage | Out-Null
& (Join-Path $assistantPackageRoot 'build.ps1') -OutputDirectory (Join-Path $stage 'bin') | Out-Null
foreach($dir in @('assistant','data','docs','media_worker','tools')){New-Item -ItemType Directory -Path (Join-Path $stage $dir)|Out-Null}
foreach($file in @('client.mjs','inbox.mjs','source-reader.ps1','state.mjs','remote.mjs','interlinked.mjs','codex.mjs','package.json','config.example.json')){Copy-Item -LiteralPath (Join-Path $assistantPackageRoot ('assistant\'+$file)) -Destination (Join-Path $stage ('assistant\'+$file))}
foreach($file in @('__init__.py','archive.py','core.py','jobs.py','vision.py','rpc.py','worker.py','config.example.json','README.md','runtime-installation.json','requirements.installation.txt')){Copy-Item -LiteralPath (Join-Path $assistantPackageRoot ('media_worker\'+$file)) -Destination (Join-Path $stage 'media_worker')}
foreach($file in @('review-notes.seed.json','review-notes.schema.json')){Copy-Item -LiteralPath (Join-Path $assistantPackageRoot ('data\'+$file)) -Destination (Join-Path $stage ('data\'+$file))}
foreach($file in @('assistant-release.md','media-inbox.md','interlinked-integration.md','video-downloader-discovery.md')){Copy-Item -LiteralPath (Join-Path $assistantPackageRoot ('docs\'+$file)) -Destination (Join-Path $stage ('docs\'+$file))}
Copy-Item -LiteralPath (Join-Path $assistantPackageRoot 'README.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $assistantPackageRoot 'tools\Install-Media-Inbox.ps1') -Destination (Join-Path $stage 'tools')
@'
ED-IT Buddy 0.4.1 PREVIEW — editing assistant

Open bin\ED-IT Buddy.exe. Keep all folders together. Requires Windows x64,
.NET Framework4.8 and Node22.13+ (EDIT_BUDDY_NODE can select an existing Node).
Other Manager services connect explicitly. The Media inbox independently reconnects
to MSI while Buddy runs after you opt into sending or watching local videos. Existing review notes remain in their prior location; this separate
package starts with its own review data. The task broker stores tasks under
%LOCALAPPDATA%\ED-IT Buddy\assistant, shared between package versions.

This is a development preview. Live workflow checks have passed on the configured
workstations; real full-corpus retrieval evaluation remains incomplete. See
docs\assistant-release.md for actual checks and remaining acceptance work.
No keys, personal media, task state, source catalog, or models are included.
The remote worker, INTERLINKED companion and Codex connection must be accepted
separately before the complete requested workflow can run.
'@ | Set-Content -LiteralPath (Join-Path $stage 'PREVIEW.txt') -Encoding UTF8
$files=[ordered]@{}
foreach($f in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName){if(($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Links are not packaged.'};$relative=$f.FullName.Substring($stage.Length+1).Replace('\','/');$files[$relative]=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
@{format='buddy-assistant-package-v1';version='0.4.1-preview';release_verified=$false;files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding UTF8
& (Join-Path $assistantPackageRoot 'tests\check-assistant-package.ps1') -PackageDirectory $stage
New-Item -ItemType Directory -Path (Join-Path $assistantPackageRoot 'dist') -Force|Out-Null
Move-Item -LiteralPath $stage -Destination $output
Compress-Archive -LiteralPath $output -DestinationPath ($output+'.zip')
Write-Output ('Preview: '+$output)
Write-Output ('ZIP: '+$output+'.zip')

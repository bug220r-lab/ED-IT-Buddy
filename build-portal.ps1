[CmdletBinding()]
param([string]$OutputDirectory = '.build\portal-dev')
$ErrorActionPreference='Stop'
$portalBuildRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$portalOutput=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $portalBuildRoot $OutputDirectory))}
if(!$portalOutput.StartsWith($portalBuildRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build output must be inside this workspace.'}
if($portalOutput.Substring($portalBuildRoot.Length+1).Split('\')[0] -notin @('.build','bin','dist')){throw 'Choose a build output directory.'}
$portalFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$portalCompiler=Join-Path $portalFramework 'csc.exe'
$portalArgs=@('/nologo','/platform:x64','/optimize+','/langversion:5','/utf8output','/codepage:65001')
foreach($reference in @('System.dll','System.Core.dll','System.Security.dll','System.Drawing.dll','System.Web.Extensions.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationClient.dll','UIAutomationTypes.dll')){
    $path=Join-Path $portalFramework $reference
    if(!(Test-Path -LiteralPath $path)){$path=Join-Path (Join-Path $portalFramework 'WPF') $reference}
    $portalArgs+='/reference:'+$path
}
$portalArgs+='/win32manifest:'+(Join-Path $portalBuildRoot 'src\app.manifest')
$portalArgs+='/win32icon:'+(Join-Path $portalBuildRoot 'src\assets\ed-it-buddy.ico')
$portalArgs+='/resource:'+(Join-Path $portalBuildRoot 'src\assets\ed-it-buddy.ico')+',EditBuddy.ApplicationIcon'
$portalSources=@(Get-ChildItem -LiteralPath (Join-Path $portalBuildRoot 'src') -Filter '*.cs' -File | Where-Object {$_.Name -notin @('App.cs','ReviewStore.cs','ProbeMain.cs')} | ForEach-Object {$_.FullName})
$portalStage=Join-Path $portalBuildRoot ('.build\portal-compile-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $portalStage -Force | Out-Null
foreach($target in @(@('Portal','winexe','PortalMain'),@('Bridge','exe','PortalBridgeMain'),@('EditorWorker','exe','MagixEditingWorkerMain'))){
    $file='ED-IT Buddy.'+$target[0]+'.exe'
    & $portalCompiler @portalArgs ('/target:'+$target[1]) ('/main:EditBuddy.'+$target[2]) ('/out:'+(Join-Path $portalStage $file)) @portalSources
    if($LASTEXITCODE -ne 0){throw ('Portal compilation failed: '+$target[0])}
    Copy-Item -LiteralPath (Join-Path $portalBuildRoot 'src\App.config') -Destination (Join-Path $portalStage ($file+'.config'))
}
if(Test-Path -LiteralPath $portalOutput){throw 'Output already exists. Choose a fresh versioned output directory; running builds are never replaced.'}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($portalOutput)) -Force | Out-Null
Move-Item -LiteralPath $portalStage -Destination $portalOutput
Get-ChildItem -LiteralPath $portalOutput -File | ForEach-Object {[ordered]@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $portalOutput 'manifest.json') -Encoding UTF8
Write-Output ('Built '+$portalOutput)

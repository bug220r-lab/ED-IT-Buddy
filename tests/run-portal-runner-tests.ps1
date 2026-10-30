$ErrorActionPreference='Stop'
$portalRunnerRoot=Split-Path -Parent $PSScriptRoot
$portalRunnerFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$portalRunnerCompiler=Join-Path $portalRunnerFramework 'csc.exe'
$portalRunnerOutput=Join-Path $portalRunnerRoot ('.build\portal-runner-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $portalRunnerOutput | Out-Null
& $portalRunnerCompiler '/nologo' '/target:exe' '/platform:x64' '/langversion:5' '/reference:System.Web.Extensions.dll' ('/out:'+(Join-Path $portalRunnerOutput 'fixture.exe')) (Join-Path $PSScriptRoot 'PortalRunnerFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Protocol fixture compilation failed.'}
$portalRunnerArguments=@('/nologo','/target:exe','/platform:x64','/langversion:5','/main:PortalRunnerProtocolTests',('/out:'+(Join-Path $portalRunnerOutput 'tests.exe')))
foreach($portalRunnerReference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Web.Extensions.dll','WindowsBase.dll','UIAutomationClient.dll','UIAutomationTypes.dll')){
    $portalRunnerPath=Join-Path $portalRunnerFramework $portalRunnerReference
    if(!(Test-Path -LiteralPath $portalRunnerPath)){$portalRunnerPath=Join-Path (Join-Path $portalRunnerFramework 'WPF') $portalRunnerReference}
    $portalRunnerArguments+='/reference:'+$portalRunnerPath
}
$portalRunnerSources=@('PortalCore.cs','ControllerPolicy.cs','MagixController.cs','MagixEditingRunner.cs','MagixEditingWorker.cs','MagixEditingActions.cs','MagixEditingUi.cs','MagixEditingCapture.cs','MagixEditingLifetime.cs','MagixEditingOwnership.cs') | ForEach-Object{Join-Path (Join-Path $portalRunnerRoot 'src') $_}
& $portalRunnerCompiler @portalRunnerArguments @portalRunnerSources (Join-Path $PSScriptRoot 'PortalRunnerProtocolTests.cs')
if($LASTEXITCODE -ne 0){throw 'Runner tests compilation failed.'}
& (Join-Path $portalRunnerOutput 'tests.exe') (Join-Path $portalRunnerOutput 'fixture.exe') (Join-Path $portalRunnerOutput 'data')
if($LASTEXITCODE -ne 0){throw 'Runner protocol/lifetime tests failed.'}

$ErrorActionPreference='Stop'
$ownershipRoot=Split-Path -Parent $PSScriptRoot
$ownershipFramework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$ownershipCompiler=Join-Path $ownershipFramework 'csc.exe'
$ownershipOutput=Join-Path $ownershipRoot ('.build\magix-ownership-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $ownershipOutput | Out-Null
$ownershipArguments=@('/nologo','/target:exe','/platform:x64','/langversion:5','/main:MagixOwnershipTests',('/out:'+(Join-Path $ownershipOutput 'tests.exe')))
foreach($ownershipReference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Web.Extensions.dll','WindowsBase.dll','UIAutomationClient.dll','UIAutomationTypes.dll')) {
    $ownershipReferencePath=Join-Path $ownershipFramework $ownershipReference
    if(!(Test-Path -LiteralPath $ownershipReferencePath)){$ownershipReferencePath=Join-Path (Join-Path $ownershipFramework 'WPF') $ownershipReference}
    $ownershipArguments+='/reference:'+$ownershipReferencePath
}
$ownershipSources=@('PortalCore.cs','ControllerPolicy.cs','MagixController.cs') | ForEach-Object{Join-Path (Join-Path $ownershipRoot 'src') $_}
$ownershipSources+=Get-ChildItem -LiteralPath (Join-Path $ownershipRoot 'src') -Filter 'MagixEditing*.cs' -File | ForEach-Object {$_.FullName}
& $ownershipCompiler @ownershipArguments @ownershipSources (Join-Path $PSScriptRoot 'MagixOwnershipTests.cs')
if($LASTEXITCODE -ne 0){throw 'Project ownership tests compilation failed.'}
$ownershipData=Join-Path $ownershipOutput 'data'
$ownershipJunctionTarget=Join-Path $ownershipData 'junction-target'
$ownershipJunctionRoot=Join-Path $ownershipData 'reparse'
New-Item -ItemType Directory -Path $ownershipJunctionTarget,$ownershipJunctionRoot | Out-Null
New-Item -ItemType Junction -Path (Join-Path $ownershipJunctionRoot 'link') -Target $ownershipJunctionTarget | Out-Null
& (Join-Path $ownershipOutput 'tests.exe') $ownershipData
if($LASTEXITCODE -ne 0){throw 'Project ownership tests failed.'}

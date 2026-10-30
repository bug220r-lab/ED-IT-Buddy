$ErrorActionPreference = 'Stop'
$portalBridgeRoot = Split-Path -Parent $PSScriptRoot
$portalBridgeOutput = Join-Path $portalBridgeRoot ('.build\portal-bridge-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $portalBridgeOutput | Out-Null
$portalBridgeCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $portalBridgeCompiler '/nologo' '/target:exe' '/platform:x64' '/langversion:5' '/main:PortalBridgeTests' '/reference:System.Core.dll' '/reference:System.Web.Extensions.dll' ("/out:" + (Join-Path $portalBridgeOutput 'PortalBridgeTests.exe')) (Join-Path $portalBridgeRoot 'src\PortalCore.cs') (Join-Path $portalBridgeRoot 'src\PortalPipeServer.cs') (Join-Path $portalBridgeRoot 'src\PortalBridgeMain.cs') (Join-Path $PSScriptRoot 'PortalBridgeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed to compile.' }
& (Join-Path $portalBridgeOutput 'PortalBridgeTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed.' }

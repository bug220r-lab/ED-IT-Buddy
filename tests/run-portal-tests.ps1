$ErrorActionPreference = 'Stop'
$portalTestRoot = Split-Path -Parent $PSScriptRoot
$portalTestOutput = Join-Path $portalTestRoot ('.build\portal-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $portalTestOutput | Out-Null
$portalCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $portalCompiler '/nologo' '/target:exe' '/platform:x64' '/langversion:5' '/reference:System.Core.dll' '/reference:System.Web.Extensions.dll' ("/out:" + (Join-Path $portalTestOutput 'PortalCoreTests.exe')) (Join-Path $portalTestRoot 'src\PortalCore.cs') (Join-Path $portalTestRoot 'src\PortalPipeServer.cs') (Join-Path $PSScriptRoot 'PortalCoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Portal tests failed to compile.' }
& (Join-Path $portalTestOutput 'PortalCoreTests.exe') (Join-Path $portalTestOutput 'data')
if ($LASTEXITCODE -ne 0) { throw 'Portal tests failed.' }

$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'ControllerPolicyTests.exe'
& $compiler '/nologo' '/target:exe' '/platform:x64' '/langversion:5' '/reference:System.Core.dll' "/out:$output" (Join-Path $testRoot 'src\ControllerPolicy.cs') (Join-Path $PSScriptRoot 'ControllerPolicyTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Controller policy tests failed to compile.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Controller policy tests failed.' }

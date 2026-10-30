$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
$output = Join-Path $PSScriptRoot 'ReviewStoreTests.exe'
& $compiler '/nologo' '/target:exe' '/platform:x64' '/langversion:5' "/reference:$frameworkDir\System.Web.Extensions.dll" "/out:$output" (Join-Path $testRoot 'src\ReviewStore.cs') (Join-Path $PSScriptRoot 'ReviewStoreTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'ReviewStore tests failed to compile.' }
& $output $testRoot
if ($LASTEXITCODE -ne 0) { throw 'ReviewStore regression tests failed; see results above.' }

$ErrorActionPreference = 'Stop'
$buddyRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $buddyRoot ('.build\portal-edl-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$program = Join-Path $testRoot 'PortalEdlTests.exe'
& $compiler /nologo /target:exe /langversion:5 ('/out:' + $program) (Join-Path $buddyRoot 'src\PortalEdl.cs') (Join-Path $PSScriptRoot 'PortalEdlTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'EDL test compilation failed.' }
& $program (Join-Path $testRoot 'fixtures')
if ($LASTEXITCODE -ne 0) { throw 'EDL tests failed.' }

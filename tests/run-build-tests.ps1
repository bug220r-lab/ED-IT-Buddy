$ErrorActionPreference = 'Stop'
$buddyRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $buddyRoot ('.build\build-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'src') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $fixture 'bin') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $buddyRoot 'build.ps1') -Destination (Join-Path $fixture 'build.ps1')
Copy-Item -LiteralPath (Join-Path $buddyRoot 'src\assets') -Destination (Join-Path $fixture 'src\assets') -Recurse
foreach ($name in @('app.manifest', 'App.config', 'AssemblyInfo.cs')) {
    Copy-Item -LiteralPath (Join-Path $buddyRoot "src\$name") -Destination (Join-Path $fixture "src\$name")
}
[IO.File]::WriteAllText((Join-Path $fixture 'src\ProbeMain.cs'), 'internal class ProbeMain { private static void Main() { } }')
[IO.File]::WriteAllText((Join-Path $fixture 'src\ReviewStore.cs'), '// Empty packaging fixture; no production app is executed.')
[IO.File]::WriteAllText((Join-Path $fixture 'src\App.cs'), 'deliberate compilation error')
[IO.File]::WriteAllText((Join-Path $fixture 'bin\ED-IT Buddy.exe'), 'previous companion')
[IO.File]::WriteAllText((Join-Path $fixture 'bin\ED-IT Buddy.Probe.exe'), 'previous helper')
$failed = $false
try { & (Join-Path $fixture 'build.ps1') -OutputDirectory 'bin' *> (Join-Path $fixture 'expected-build-error.txt') }
catch { $failed = $true }
if (!$failed) { throw 'Expected invalid source to fail compilation.' }
if ([IO.File]::ReadAllText((Join-Path $fixture 'bin\ED-IT Buddy.exe')) -ne 'previous companion') { throw 'Failed compile replaced the companion.' }
if ([IO.File]::ReadAllText((Join-Path $fixture 'bin\ED-IT Buddy.Probe.exe')) -ne 'previous helper') { throw 'Failed compile replaced the helper.' }
Write-Output 'PASS: compilation failure leaves both prior executables unchanged.'

[IO.File]::WriteAllText((Join-Path $fixture 'src\App.cs'), 'internal class App { [System.STAThread] private static void Main() { } }')
& (Join-Path $fixture 'build.ps1') -OutputDirectory 'bin' | Out-Null
foreach ($name in @('ED-IT Buddy.exe', 'ED-IT Buddy.Probe.exe')) {
    $file = Join-Path $fixture "bin\$name"
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($file).FileVersion -ne '0.3.1.0') { throw "Version not embedded: $name" }
    if (!(Test-Path -LiteralPath ($file + '.config'))) { throw "Runtime config not published: $name" }
}
$backups = @(Get-ChildItem -LiteralPath (Join-Path $fixture '.build') -Directory -Filter 'previous-*')
if ($backups.Count -ne 1 -or [IO.File]::ReadAllText((Join-Path $backups[0].FullName 'ED-IT Buddy.exe')) -ne 'previous companion') { throw 'Previous build backup was not retained.' }
Write-Output 'PASS: successful compilation publishes the complete versioned build and retains the previous files.'

$blocked = $false
try { & (Join-Path $fixture 'build.ps1') -OutputDirectory 'src' | Out-Null } catch { $blocked = $true }
if (!$blocked) { throw 'Build accepted source directory as output.' }
$blocked = $false
try { & (Join-Path $fixture 'build.ps1') -OutputDirectory '..\outside-fixture' | Out-Null } catch { $blocked = $true }
if (!$blocked) { throw 'Build accepted an output path outside its workspace.' }
Write-Output 'PASS: source directory and workspace-escaping output paths are rejected.'
Write-Output "Build-test fixtures retained at $fixture. No generated program was launched."

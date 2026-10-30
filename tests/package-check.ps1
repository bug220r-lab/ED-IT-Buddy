[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$expected = @(
    'bin/ED-IT Buddy.exe',
    'bin/ED-IT Buddy.Probe.exe',
    'bin/ED-IT Buddy.exe.config',
    'bin/ED-IT Buddy.Probe.exe.config',
    'data/review-notes.seed.json',
    'data/review-notes.schema.json',
    'docs/design.md',
    'docs/control-map.md',
    'docs/review-workflow.md',
    'README.md',
    'PORTABLE.txt',
    'package-manifest.json'
)
$actual = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object { $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/') })
$difference = @(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object))
if ($difference.Count -gt 0) { throw ('Unexpected portable package file list: ' + ($difference | Out-String)) }
$manifest = Get-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.packageVersion -ne '0.1.0' -or $manifest.architecture -ne 'x64') { throw 'Unexpected package version or architecture.' }
foreach ($entry in $manifest.files.PSObject.Properties) {
    if ($entry.Name -notin $expected -or $entry.Name -eq 'package-manifest.json') { throw 'Unexpected hashed package entry.' }
    $hash = (Get-FileHash -LiteralPath (Join-Path $packageRoot $entry.Name) -Algorithm SHA256).Hash
    if ($hash -ne $entry.Value) { throw "Hash mismatch: $($entry.Name)" }
}
if (@($manifest.files.PSObject.Properties).Count -ne $expected.Count - 1) { throw 'Hash manifest is incomplete.' }
foreach ($name in @('ED-IT Buddy.exe', 'ED-IT Buddy.Probe.exe')) {
    $executable = Join-Path $packageRoot "bin\$name"
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    if ($version.FileVersion -ne '0.1.0.0') { throw "Unexpected executable version: $name" }
    $bytes = [IO.File]::ReadAllBytes($executable)
    if ($bytes.Length -lt 128 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90) { throw "Invalid PE file: $name" }
    $peOffset = [BitConverter]::ToInt32($bytes, 60)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) { throw "Executable is not x64: $name" }
    [xml]$config = Get-Content -LiteralPath ($executable + '.config') -Raw
    if ($config.configuration.startup.supportedRuntime.sku -ne '.NETFramework,Version=v4.8') { throw "Unexpected runtime configuration: $name" }
}
$seed = Get-Content -LiteralPath (Join-Path $packageRoot 'data\review-notes.seed.json') -Raw | ConvertFrom-Json
if ($seed.schemaVersion -ne 1 -or $seed.mappingConfirmed -or $seed.exportPath -or $seed.projectPath) { throw 'Seed data must not contain an active user project mapping.' }
Write-Output 'PASS: portable file allowlist, complete SHA-256 manifest, x64 executable versions, runtime configuration, and clean seed data. No program was launched.'

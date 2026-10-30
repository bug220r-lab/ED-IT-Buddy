[CmdletBinding()]
param(
    [string]$PackageName = 'ED-IT-Buddy-0.1.0'
)

$ErrorActionPreference = 'Stop'
if ($PackageName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or $PackageName -match '\.\.') {
    throw 'PackageName must be a short file name using letters, numbers, hyphens, underscores, and periods.'
}
$buddyRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$distDir = Join-Path $buddyRoot 'dist'
$packageDir = [IO.Path]::GetFullPath((Join-Path $distDir $PackageName))
$zipPath = $packageDir + '.zip'
if ((Test-Path -LiteralPath $packageDir) -or (Test-Path -LiteralPath $zipPath)) {
    throw "That package already exists. Choose a fresh -PackageName; no files were replaced: $packageDir"
}
$stagingRoot = Join-Path $buddyRoot ('.build\package-' + [Guid]::NewGuid().ToString('N'))
$stagingPackage = Join-Path $stagingRoot $PackageName
New-Item -ItemType Directory -Path $stagingPackage -Force | Out-Null
& (Join-Path $buddyRoot 'build.ps1') -OutputDirectory (Join-Path $stagingPackage 'bin')
Copy-Item -LiteralPath (Join-Path $buddyRoot 'README.md') -Destination (Join-Path $stagingPackage 'README.md')
New-Item -ItemType Directory -Path (Join-Path $stagingPackage 'data') -Force | Out-Null
foreach ($name in @('review-notes.seed.json', 'review-notes.schema.json')) {
    Copy-Item -LiteralPath (Join-Path $buddyRoot "data\$name") -Destination (Join-Path $stagingPackage "data\$name")
}
New-Item -ItemType Directory -Path (Join-Path $stagingPackage 'docs') -Force | Out-Null
foreach ($name in @('design.md', 'control-map.md', 'review-workflow.md')) {
    Copy-Item -LiteralPath (Join-Path $buddyRoot "docs\$name") -Destination (Join-Path $stagingPackage "docs\$name")
}
$packageNote = @'
ED-IT Buddy 0.1.0 portable build

Extract the entire folder into a writable location, then open bin\ED-IT Buddy.exe.
The application creates its own user-data folder beside bin and data.
This package contains no personal review notes, logs, media, or control snapshots.
Requires Windows x64 with .NET Framework 4.8. No administrator privileges are requested.

The README build commands apply to the source workspace; source and development tests
are intentionally not included in this portable runtime package.
'@
Set-Content -LiteralPath (Join-Path $stagingPackage 'PORTABLE.txt') -Value $packageNote -Encoding UTF8

# Only the explicit runtime list above is included; never copy the workspace recursively.
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $stagingPackage -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($stagingPackage.Length + 1).Replace('\', '/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest = [ordered]@{
    packageVersion = '0.1.0'
    architecture = 'x64'
    targetFramework = '.NET Framework 4.8'
    files = $hashes
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stagingPackage 'package-manifest.json') -Encoding UTF8

& (Join-Path $buddyRoot 'tests\package-check.ps1') -PackageDirectory $stagingPackage
$stagingZip = Join-Path $stagingRoot ($PackageName + '.zip')
Compress-Archive -LiteralPath $stagingPackage -DestinationPath $stagingZip -CompressionLevel Optimal
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$workspacePrefix = $buddyRoot.TrimEnd('\') + '\'
foreach ($checkedPath in @($stagingPackage, $packageDir, $stagingZip, $zipPath)) {
    $resolved = [IO.Path]::GetFullPath($checkedPath)
    if (!$resolved.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved package path escaped the workspace.' }
}
Move-Item -LiteralPath $stagingPackage -Destination $packageDir
Move-Item -LiteralPath $stagingZip -Destination $zipPath
Write-Output "Portable folder: $packageDir"
Write-Output "Portable ZIP: $zipPath"

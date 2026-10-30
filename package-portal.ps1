[CmdletBinding()]
param(
    [string]$PackageName = 'ED-IT-Buddy-Portal-0.2.0',
    [string]$BuildDirectory,
    [string]$NodeExecutable = (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe')
)
$ErrorActionPreference = 'Stop'
if ($PackageName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or $PackageName.Contains('..')) { throw 'Choose a short, ordinary package name.' }
$portalPackageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$portalPackagePrefix = $portalPackageRoot.TrimEnd('\') + '\'
$portalDist = Join-Path $portalPackageRoot 'dist'
$portalFinal = [IO.Path]::GetFullPath((Join-Path $portalDist $PackageName))
$portalZip = $portalFinal + '.zip'
if ((Test-Path -LiteralPath $portalFinal) -or (Test-Path -LiteralPath $portalZip)) { throw 'That package already exists. Choose a new PackageName; existing packages are never replaced.' }
$portalStageRoot = Join-Path $portalPackageRoot ('.build\portal-package-' + [Guid]::NewGuid().ToString('N'))
$portalStage = Join-Path $portalStageRoot $PackageName
foreach ($portalChecked in @($portalFinal, $portalZip, $portalStageRoot, $portalStage)) {
    if (![IO.Path]::GetFullPath($portalChecked).StartsWith($portalPackagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved package path escaped this workspace.' }
}
New-Item -ItemType Directory -Path $portalStage -Force | Out-Null
$portalWindows = Join-Path $portalStage 'windows'
if ($BuildDirectory) {
    $portalBuild = if ([IO.Path]::IsPathRooted($BuildDirectory)) { [IO.Path]::GetFullPath($BuildDirectory) } else { [IO.Path]::GetFullPath((Join-Path $portalPackageRoot $BuildDirectory)) }
    if (!$portalBuild.StartsWith($portalPackagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'BuildDirectory must be inside the source workspace.' }
    New-Item -ItemType Directory -Path $portalWindows | Out-Null
    foreach ($portalName in @('ED-IT Buddy.Portal.exe', 'ED-IT Buddy.Portal.exe.config', 'ED-IT Buddy.Bridge.exe', 'ED-IT Buddy.Bridge.exe.config', 'ED-IT Buddy.EditorWorker.exe', 'ED-IT Buddy.EditorWorker.exe.config', 'manifest.json')) {
        $portalSource = Join-Path $portalBuild $portalName
        if (!(Test-Path -LiteralPath $portalSource -PathType Leaf) -or ((Get-Item -LiteralPath $portalSource).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Missing or redirected binary: $portalName" }
        Copy-Item -LiteralPath $portalSource -Destination (Join-Path $portalWindows $portalName)
    }
} else {
    & (Join-Path $portalPackageRoot 'build-portal.ps1') -OutputDirectory $portalWindows
}
$portalAllowedBinaries = @('ED-IT Buddy.Portal.exe', 'ED-IT Buddy.Portal.exe.config', 'ED-IT Buddy.Bridge.exe', 'ED-IT Buddy.Bridge.exe.config', 'ED-IT Buddy.EditorWorker.exe', 'ED-IT Buddy.EditorWorker.exe.config')
$portalBuildManifest = Get-Content -LiteralPath (Join-Path $portalWindows 'manifest.json') -Raw | ConvertFrom-Json
if (@($portalBuildManifest).Count -ne $portalAllowedBinaries.Count -or @($portalBuildManifest.file | Sort-Object -Unique).Count -ne $portalAllowedBinaries.Count) { throw 'Build manifest does not contain exactly the six runtime files.' }
foreach ($portalEntry in $portalBuildManifest) {
    if ($portalEntry.file -notin $portalAllowedBinaries -or (Get-FileHash -LiteralPath (Join-Path $portalWindows $portalEntry.file) -Algorithm SHA256).Hash -ine $portalEntry.sha256) { throw 'Build manifest verification failed.' }
}
$portalAdapter = Join-Path $portalStage 'portal'
New-Item -ItemType Directory -Path $portalAdapter | Out-Null
foreach ($portalName in @('client.mjs', 'server.mjs', 'transport.mjs', 'schemas.mjs', 'package.json', 'pnpm-lock.yaml', 'README.md', 'config.example.json', 'codex-config.example.toml', 'demo-sequence.example.json')) {
    Copy-Item -LiteralPath (Join-Path $portalPackageRoot ('portal\' + $portalName)) -Destination (Join-Path $portalAdapter $portalName)
}
# Flatten only the three pinned production packages. Never copy pnpm links, caches,
# arbitrary node_modules content, configuration overrides, credentials, or user data.
$portalDependencies = @(
    @{ Name='@modelcontextprotocol/server'; Version='2.4.0'; Store='@modelcontextprotocol+server@2.4.0' },
    @{ Name='@modelcontextprotocol/core'; Version='2.4.0'; Store='@modelcontextprotocol+core@2.4.0' },
    @{ Name='zod'; Version='4.6.7'; Store='zod@4.6.7' }
)
foreach ($portalDependency in $portalDependencies) {
    $portalSource = Join-Path $portalPackageRoot ('portal\node_modules\.pnpm\' + $portalDependency.Store + '\node_modules\' + $portalDependency.Name.Replace('/', '\'))
    if (!(Test-Path -LiteralPath $portalSource -PathType Container)) { throw 'Restore pinned portal dependencies with pnpm install --frozen-lockfile --ignore-scripts before packaging.' }
    $portalMetadata = Get-Content -LiteralPath (Join-Path $portalSource 'package.json') -Raw | ConvertFrom-Json
    if ($portalMetadata.name -ne $portalDependency.Name -or $portalMetadata.version -ne $portalDependency.Version) { throw 'Installed dependency differs from the pinned production version.' }
    $portalItems = @((Get-Item -LiteralPath $portalSource)) + @(Get-ChildItem -LiteralPath $portalSource -Force -Recurse)
    if (@($portalItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Production package contains a filesystem link; inspect it before packaging.' }
    $portalDestination = Join-Path $portalAdapter ('node_modules\' + $portalDependency.Name.Replace('/', '\'))
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($portalDestination)) -Force | Out-Null
    Copy-Item -LiteralPath $portalSource -Destination $portalDestination -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $portalStage 'docs'), (Join-Path $portalStage 'tools') | Out-Null
Copy-Item -LiteralPath (Join-Path $portalPackageRoot 'docs\portal.md') -Destination (Join-Path $portalStage 'docs\portal.md')
Copy-Item -LiteralPath (Join-Path $portalPackageRoot 'tools\deploy-portal.ps1') -Destination (Join-Path $portalStage 'tools\deploy-portal.ps1')
if (!(Test-Path -LiteralPath $NodeExecutable -PathType Leaf)) { throw 'Supply the installed Node executable; the runtime is not included in this package.' }
Push-Location $portalAdapter
try {
    & $NodeExecutable '--input-type=module' '-e' 'await import("@modelcontextprotocol/server"); await import("@modelcontextprotocol/server/stdio"); await import("zod"); console.log("Packaged MCP dependencies load.");'
    if ($LASTEXITCODE -ne 0) { throw 'Packaged Node dependency smoke test failed.' }
} finally { Pop-Location }
$portalHashes = [ordered]@{}
foreach ($portalFile in Get-ChildItem -LiteralPath $portalStage -File -Force -Recurse | Sort-Object FullName) {
    $portalRelative = $portalFile.FullName.Substring($portalStage.Length + 1).Replace('\', '/')
    $portalHashes[$portalRelative] = (Get-FileHash -LiteralPath $portalFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{
    package_version='0.2.0'; architecture='x64'; framework='.NET Framework 4.8'; node_required='>=22.13'; node_bundled=$false;
    editing_capabilities='Query get_capabilities on the running MSI host; package inclusion is not live verification.';
    files=$portalHashes
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $portalStage 'package-manifest.json') -Encoding UTF8
$portalStageZip = Join-Path $portalStageRoot ($PackageName + '.zip')
Compress-Archive -LiteralPath $portalStage -DestinationPath $portalStageZip -CompressionLevel Optimal
New-Item -ItemType Directory -Path $portalDist -Force | Out-Null
# Every move target was resolved and checked above; no deletion or overwrite occurs.
Move-Item -LiteralPath $portalStage -Destination $portalFinal
Move-Item -LiteralPath $portalStageZip -Destination $portalZip
Write-Output "Portal package: $portalFinal"
Write-Output "Portal ZIP: $portalZip"

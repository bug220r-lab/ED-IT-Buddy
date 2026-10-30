[CmdletBinding()]
param(
    [string]$OutputDirectory = 'bin'
)

$ErrorActionPreference = 'Stop'
$buddyRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$workspacePrefix = $buddyRoot.TrimEnd('\') + '\'
$outputDir = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $buddyRoot $OutputDirectory))
}
if (!$outputDir.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build output must be a folder inside this workspace.'
}
$relativeOutput = $outputDir.Substring($workspacePrefix.Length)
$outputTop = $relativeOutput.Split('\')[0]
if ($outputTop -notin @('bin', 'dist', '.build') -or $relativeOutput -in @('dist', '.build')) {
    throw 'Choose bin or a subfolder of dist or .build for build output.'
}

$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework compiler was not found.' }
$buildId = [Guid]::NewGuid().ToString('N')
$stagingDir = [IO.Path]::GetFullPath((Join-Path $buddyRoot ".build\compile-$buildId"))
$backupDir = [IO.Path]::GetFullPath((Join-Path $buddyRoot ".build\previous-$buildId"))
foreach ($checkedPath in @($stagingDir, $backupDir, $outputDir)) {
    if (!$checkedPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved build path escaped the workspace: $checkedPath"
    }
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

$references = @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll')
$arguments = @('/nologo', '/platform:x64', '/optimize+', '/langversion:5', '/utf8output', '/codepage:65001')
foreach ($reference in $references) {
    $referencePath = Join-Path $frameworkDir $reference
    if (!(Test-Path -LiteralPath $referencePath)) { $referencePath = Join-Path (Join-Path $frameworkDir 'WPF') $reference }
    if (!(Test-Path -LiteralPath $referencePath)) { throw "Missing framework reference: $reference" }
    $arguments += "/reference:$referencePath"
}
$arguments += '/win32manifest:' + (Join-Path $buddyRoot 'src\app.manifest')
$arguments += '/win32icon:' + (Join-Path $buddyRoot 'src\assets\ed-it-buddy.ico')
$arguments += '/resource:' + (Join-Path $buddyRoot 'src\assets\ed-it-buddy.ico') + ',EditBuddy.ApplicationIcon'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $buddyRoot 'src') -File -Filter '*.cs' | Where-Object { $_.Name -notlike 'Portal*.cs' -and $_.Name -notlike 'MagixEditing*.cs' } | Sort-Object Name)
$appSources = @($sources | Where-Object { $_.Name -ne 'ProbeMain.cs' } | ForEach-Object { $_.FullName })
$probeSources = @($sources | Where-Object { $_.Name -notin @('App.cs', 'ReviewStore.cs') } | ForEach-Object { $_.FullName })

& $compiler @arguments '/target:exe' "/out:$stagingDir\ED-IT Buddy.Probe.exe" @probeSources
if ($LASTEXITCODE -ne 0) { throw "Probe build failed. Existing output was not changed. Diagnostic staging: $stagingDir" }
& $compiler @arguments '/target:winexe' "/out:$stagingDir\ED-IT Buddy.exe" @appSources
if ($LASTEXITCODE -ne 0) { throw "Companion build failed. Existing output was not changed. Diagnostic staging: $stagingDir" }
Copy-Item -LiteralPath (Join-Path $buddyRoot 'src\App.config') -Destination (Join-Path $stagingDir 'ED-IT Buddy.exe.config')
Copy-Item -LiteralPath (Join-Path $buddyRoot 'src\App.config') -Destination (Join-Path $stagingDir 'ED-IT Buddy.Probe.exe.config')

$knownOutputs = @('ED-IT Buddy.exe', 'ED-IT Buddy.Probe.exe', 'ED-IT Buddy.exe.config', 'ED-IT Buddy.Probe.exe.config')
$hasPreviousOutput = Test-Path -LiteralPath $outputDir
if ($hasPreviousOutput) {
    if (!(Test-Path -LiteralPath $outputDir -PathType Container)) { throw 'Build output is not a directory.' }
    $existingOutputs = @(Get-ChildItem -LiteralPath $outputDir -Force)
    if (@($existingOutputs | Where-Object { $_.PSIsContainer -or $_.Name -notin $knownOutputs }).Count -gt 0) {
        throw "Build output contains other files. Choose a fresh OutputDirectory; existing files were not changed: $outputDir"
    }
    # A loaded executable normally denies this access. Never stop its process to rebuild it.
    foreach ($existing in $existingOutputs) {
        try {
            $stream = [IO.File]::Open($existing.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $stream.Dispose()
        } catch {
            throw "Build output is in use. Close Buddy yourself or choose a fresh OutputDirectory: $($existing.FullName)"
        }
    }
}
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($outputDir)) -Force | Out-Null
if ($hasPreviousOutput) { Move-Item -LiteralPath $outputDir -Destination $backupDir }
try {
    Move-Item -LiteralPath $stagingDir -Destination $outputDir
} catch {
    if ($hasPreviousOutput -and !(Test-Path -LiteralPath $outputDir)) { Move-Item -LiteralPath $backupDir -Destination $outputDir }
    throw
}
Write-Output "Built $outputDir\ED-IT Buddy.exe"
if ($hasPreviousOutput) { Write-Verbose "Previous build retained at $backupDir" }

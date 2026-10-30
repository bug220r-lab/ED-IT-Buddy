[CmdletBinding()]
param([string]$PackageName = '')

$ErrorActionPreference = 'Stop'
# The Manager needs its broker and media contract files. Keep the historical
# entry point, but never create a new executable-only legacy package.
$assistantPackager = Join-Path $PSScriptRoot 'package-assistant.ps1'
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    & $assistantPackager
} else {
    & $assistantPackager -PackageName $PackageName
}

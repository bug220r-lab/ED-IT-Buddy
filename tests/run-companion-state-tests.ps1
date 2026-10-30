$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDir 'csc.exe'
$arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/main:CompanionStateTests')
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationClient.dll','UIAutomationTypes.dll')) {
    $referencePath = Join-Path $frameworkDir $reference
    if (!(Test-Path -LiteralPath $referencePath)) { $referencePath = Join-Path (Join-Path $frameworkDir 'WPF') $reference }
    $arguments += "/reference:$referencePath"
}
$output = Join-Path $PSScriptRoot 'CompanionStateTests.exe'
& $compiler @arguments "/out:$output" (Join-Path $testRoot 'src\MagixController.cs') (Join-Path $testRoot 'src\ControllerPolicy.cs') (Join-Path $testRoot 'src\MatrixTheme.cs') (Join-Path $testRoot 'src\App.cs') (Join-Path $testRoot 'src\ReviewStore.cs') (Join-Path $PSScriptRoot 'CompanionStateTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Companion state tests failed to compile.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Companion state tests failed.' }

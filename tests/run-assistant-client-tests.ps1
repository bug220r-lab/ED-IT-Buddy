[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$testRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $testRoot ('.build\assistant-client-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$arguments = @('/nologo','/target:exe','/platform:x64','/langversion:5','/utf8output','/codepage:65001','/main:AssistantProcessClientTests')
foreach ($reference in @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')) {
    $path = Join-Path $framework $reference
    if (!(Test-Path -LiteralPath $path)) { $path = Join-Path (Join-Path $framework 'WPF') $reference }
    $arguments += '/reference:' + $path
}
$output = Join-Path $fixture 'AssistantProcessClientTests.exe'
& (Join-Path $framework 'csc.exe') @arguments ('/out:' + $output) (Join-Path $testRoot 'src\AssistantProcessClient.cs') (Join-Path $testRoot 'src\AssistantView.cs') (Join-Path $testRoot 'src\IndexProgressPanel.cs') (Join-Path $testRoot 'src\MatrixTheme.cs') (Join-Path $PSScriptRoot 'AssistantProcessClientTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Assistant client tests failed to compile.' }
& $output $fixture
if ($LASTEXITCODE -ne 0) { throw 'Assistant client tests failed.' }

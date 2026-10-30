$ErrorActionPreference = 'Stop'
$buddyTestRoot = Split-Path -Parent $PSScriptRoot
$buddyFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$buddyCompiler = Join-Path $buddyFramework 'csc.exe'
$buddyOutput = Join-Path $buddyTestRoot ('.build\magix-policy-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buddyOutput | Out-Null
$buddyArguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:5', '/main:MagixEditingPolicyTests', ('/out:' + (Join-Path $buddyOutput 'tests.exe')))
foreach ($buddyReference in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll', 'WindowsBase.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll')) {
    $buddyReferencePath = Join-Path $buddyFramework $buddyReference
    if (!(Test-Path -LiteralPath $buddyReferencePath)) { $buddyReferencePath = Join-Path (Join-Path $buddyFramework 'WPF') $buddyReference }
    $buddyArguments += '/reference:' + $buddyReferencePath
}
$buddySources = @('PortalCore.cs', 'ControllerPolicy.cs', 'MagixController.cs', 'MagixEditingRunner.cs', 'MagixEditingWorker.cs', 'MagixEditingActions.cs', 'MagixEditingUi.cs', 'MagixEditingCapture.cs', 'MagixEditingLifetime.cs', 'MagixEditingOwnership.cs') | ForEach-Object { Join-Path (Join-Path $buddyTestRoot 'src') $_ }
& $buddyCompiler @buddyArguments @buddySources (Join-Path $PSScriptRoot 'MagixEditingPolicyTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'MAGIX editing policy tests did not compile.' }
& (Join-Path $buddyOutput 'tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'MAGIX editing policy tests failed.' }

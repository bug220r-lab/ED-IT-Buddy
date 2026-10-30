$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $root ('.build\index-progress-tests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$argsList=@('/nologo','/target:exe','/platform:x64','/langversion:5','/utf8output','/codepage:65001','/main:IndexProgressTests')
foreach($reference in @('System.dll','System.Core.dll','System.Xaml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')){
 $p=Join-Path $framework $reference
 if(!(Test-Path -LiteralPath $p)){$p=Join-Path (Join-Path $framework 'WPF') $reference}
 $argsList+='/reference:'+$p
}
$output=Join-Path $fixture 'IndexProgressTests.exe'
& (Join-Path $framework 'csc.exe') @argsList ('/out:'+$output) (Join-Path $root 'src\IndexProgressPanel.cs') (Join-Path $root 'src\MatrixTheme.cs') (Join-Path $PSScriptRoot 'IndexProgressTests.cs')
if($LASTEXITCODE -ne 0){throw 'Index status tests did not compile'}
& $output $fixture
if($LASTEXITCODE -ne 0){throw 'Index status tests failed'}

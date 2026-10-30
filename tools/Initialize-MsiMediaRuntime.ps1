[CmdletBinding()]
param([switch]$InstallModels)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI' -or $env:USERNAME -ne 'jlewi'){throw 'Run only as MSI\jlewi on MSI.'}
$mediaInstallRoot='C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media'
if((Get-PSDrive C).Free -lt 12GB){throw 'At least 12 GiB free is required for runtime and model installation.'}
if(Test-Path -LiteralPath $mediaInstallRoot){
    $owner=Get-Content -LiteralPath (Join-Path $mediaInstallRoot 'OWNER.json') -Raw | ConvertFrom-Json
    if($owner.format -ne 'buddy-media-install-v1' -or $owner.sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Existing installation is unowned.'}
}else{
    New-Item -ItemType Directory -Path $mediaInstallRoot | Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,(New-Object Security.Principal.SecurityIdentifier('S-1-5-18')))){
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    }
    Set-Acl -LiteralPath $mediaInstallRoot -AclObject $acl
    @{format='buddy-media-install-v1';sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;computer=$env:COMPUTERNAME}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $mediaInstallRoot 'OWNER.json') -Encoding UTF8
}
foreach($dir in @('downloads','runtime','state','models')){[void][IO.Directory]::CreateDirectory((Join-Path $mediaInstallRoot $dir))}
$pythonExe=Join-Path $mediaInstallRoot 'runtime\python.exe'
if(!(Test-Path -LiteralPath $pythonExe)){
    $zip=Join-Path $mediaInstallRoot 'downloads\python-3.12.10-embed-amd64.zip'
    Invoke-WebRequest -UseBasicParsing 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $zip
    # The release page publishes this digest; verify before extracting or running.
    if((Get-FileHash -LiteralPath $zip -Algorithm MD5).Hash.ToLowerInvariant() -ne 'fe8ef205f2e9c3ba44d0cf9954e1abd3'){throw 'Python release archive checksum mismatch.'}
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $mediaInstallRoot 'runtime')
    $signature=Get-AuthenticodeSignature -LiteralPath $pythonExe
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Python Software Foundation'){throw 'Python publisher signature verification failed.'}
    [IO.File]::WriteAllText((Join-Path $mediaInstallRoot 'runtime\python312._pth'),"python312.zip`n.`nLib\site-packages`nimport site`n")
}
$site=Join-Path $mediaInstallRoot 'runtime\Lib\site-packages'
[void][IO.Directory]::CreateDirectory($site)
if(!(Test-Path -LiteralPath (Join-Path $site 'pip'))){
    $pipInfo=Invoke-RestMethod 'https://pypi.org/pypi/pip/25.2/json'
    $wheel=@($pipInfo.urls|Where-Object {$_.filename -eq 'pip-25.2-py3-none-any.whl'})[0]
    if(!$wheel -or $wheel.url -notlike 'https://files.pythonhosted.org/*'){throw 'Unexpected pip wheel source.'}
    $pipZip=Join-Path $mediaInstallRoot 'downloads\pip-25.2.zip'
    Invoke-WebRequest -UseBasicParsing $wheel.url -OutFile $pipZip
    if((Get-FileHash -LiteralPath $pipZip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wheel.digests.sha256){throw 'pip publisher checksum mismatch.'}
    Expand-Archive -LiteralPath $pipZip -DestinationPath $site
}
& $pythonExe -m pip install --disable-pip-version-check --no-input --only-binary=:all: --no-warn-script-location 'torch==2.8.0' --index-url https://download.pytorch.org/whl/cu126 --report (Join-Path $mediaInstallRoot 'downloads\torch-install.json')
if($LASTEXITCODE -ne 0){throw 'PyTorch installation failed.'}
& $pythonExe -m pip install --disable-pip-version-check --no-input --only-binary=:all: --no-warn-script-location 'transformers==4.57.1' 'Pillow==11.3.0' 'numpy==2.2.6' --report (Join-Path $mediaInstallRoot 'downloads\vision-install.json')
if($LASTEXITCODE -ne 0){throw 'Vision dependencies installation failed.'}
& $pythonExe -m pip freeze | Set-Content -LiteralPath (Join-Path $mediaInstallRoot 'runtime-lock.txt') -Encoding UTF8
if($InstallModels){
    $modelSetup=@'
import json, pathlib
from huggingface_hub import HfApi, snapshot_download
root=pathlib.Path(r'C:\Users\jlewi\AppData\Local\ED-IT-Buddy-Media')
repo='google/siglip2-base-patch16-224'
pin=root/'model-lock.json'
revision=json.loads(pin.read_text(encoding='utf-8-sig'))['revision'] if pin.exists() else HfApi().model_info(repo).sha
location=snapshot_download(repo_id=repo,revision=revision,local_dir=str(root/'models'/'siglip2-base-patch16-224'),allow_patterns=['*.json','*.safetensors','*.txt','*.model'])
pin.write_text(json.dumps({'model':repo,'revision':revision,'local_path':location},indent=2),encoding='utf-8')
print(json.dumps({'model':repo,'revision':revision,'downloaded':True}))
'@
    $modelSetup | & $pythonExe -
    if($LASTEXITCODE -ne 0){throw 'Model download failed; partial cache retained for resume.'}
}
Get-FileHash -LiteralPath $pythonExe -Algorithm SHA256 | ConvertTo-Json
Write-Output 'Buddy media runtime prepared. Existing downloader and INTERLINKED runtimes were not changed.'

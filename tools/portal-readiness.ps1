$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$expectedExe = 'C:\Program Files (x86)\Steam\steamapps\common\MAGIX\Video deluxe 2021 Plus Steam Edition\Videodeluxe.exe'
$processes = @(Get-Process -Name explorer,steam,Videodeluxe -ErrorAction SilentlyContinue | ForEach-Object {
    $image = $null
    try { $image = $_.Path } catch {}
    [ordered]@{ name=$_.ProcessName; pid=$_.Id; session_id=$_.SessionId; path=$image; title=$_.MainWindowTitle }
})
[ordered]@{
    host_name=$env:COMPUTERNAME
    identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
    user_sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    ssh_session_id=[Diagnostics.Process]::GetCurrentProcess().SessionId
    processes=$processes
    magix_exists=(Test-Path -LiteralPath $expectedExe -PathType Leaf)
    magix_version=$(if(Test-Path -LiteralPath $expectedExe){[Diagnostics.FileVersionInfo]::GetVersionInfo($expectedExe).FileVersion})
    c_free_bytes=(Get-PSDrive -Name C).Free
    framework_release=(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
    local_app_data=$env:LOCALAPPDATA
} | ConvertTo-Json -Depth 5 -Compress

[CmdletBinding()]
param([switch]$LaunchMagix,[ValidatePattern('^[A-Za-z0-9_-]+\.json$')][string]$RequestFile,[switch]$Capture,[Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{32}$')][string]$RunId)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
if($env:COMPUTERNAME -ne 'MSI' -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'This probe requires the MSI interactive desktop.'}
$portalProbeRoot=Join-Path $env:LOCALAPPDATA 'ED-IT Buddy\portal\probe'
$portalProbeWorker=Join-Path $portalProbeRoot 'ED-IT Buddy.EditorWorker.exe'
$portalProbeReceipt=Join-Path $portalProbeRoot ('snapshot-'+$RunId+$(if($Capture){'.png'}else{'.json'}))
$portalStarted=[DateTime]::UtcNow
$portalReceiptPath=Join-Path $portalProbeRoot 'latest.json'
[ordered]@{ok=$false;status='running';run_id=$RunId;started_utc=$portalStarted.ToString('o');request=$RequestFile}|ConvertTo-Json -Compress|Set-Content -LiteralPath $portalReceiptPath -Encoding UTF8
try {
    if($Capture -and $RequestFile){throw 'Capture and an editing request cannot run together.'}
    $existing=@(Get-Process -Name Videodeluxe -ErrorAction SilentlyContinue)
    if($LaunchMagix -and $existing.Count -eq 0){
        $steam=Join-Path ${env:ProgramFiles(x86)} 'Steam\steam.exe'
        Start-Process -FilePath $steam -ArgumentList '-applaunch','1363250' -WindowStyle Hidden
        Start-Sleep -Seconds 15
    }
    $portalWorkerArgs=if($RequestFile){'--request "'+(Join-Path $portalProbeRoot $RequestFile)+'" --output "'+$portalProbeReceipt+'"'}elseif($Capture){'--capture "'+$portalProbeReceipt+'"'}else{'--inspect "'+$portalProbeReceipt+'"'}
    $portalChild=Start-Process -FilePath $portalProbeWorker -ArgumentList $portalWorkerArgs -WindowStyle Hidden -PassThru
    $portalChildStart=$portalChild.StartTime.ToUniversalTime().Ticks
    if(!$portalChild.WaitForExit(45000)){
        $portalOwnedChild=Get-Process -Id $portalChild.Id -ErrorAction SilentlyContinue
        if($portalOwnedChild -and $portalOwnedChild.Path -ieq $portalProbeWorker -and $portalOwnedChild.StartTime.ToUniversalTime().Ticks -eq $portalChildStart){Stop-Process -InputObject $portalOwnedChild -ErrorAction Stop}
        throw 'Probe exceeded 45 seconds. Only its worker was stopped; MAGIX remains open. An editing request may have partially executed and must be inspected before retrying.'
    }
    if($portalChild.ExitCode -ne 0){throw ('Snapshot worker exited '+$portalChild.ExitCode)}
    if(!(Test-Path -LiteralPath $portalProbeReceipt)){throw 'Worker exited without its fresh output.'}
    [ordered]@{ok=$true;status='completed';run_id=$RunId;started_utc=$portalStarted.ToString('o');captured_utc=[DateTime]::UtcNow.ToString('o');request=$RequestFile;snapshot=$portalProbeReceipt;capture=[bool]$Capture;session_id=[Diagnostics.Process]::GetCurrentProcess().SessionId}|ConvertTo-Json -Compress|Set-Content -LiteralPath $portalReceiptPath -Encoding UTF8
} catch {
    [ordered]@{ok=$false;status='failed';run_id=$RunId;started_utc=$portalStarted.ToString('o');captured_utc=[DateTime]::UtcNow.ToString('o');request=$RequestFile;input_may_have_occurred=[bool]$RequestFile;error=$_.Exception.Message;session_id=[Diagnostics.Process]::GetCurrentProcess().SessionId}|ConvertTo-Json -Compress|Set-Content -LiteralPath $portalReceiptPath -Encoding UTF8
    exit 1
}

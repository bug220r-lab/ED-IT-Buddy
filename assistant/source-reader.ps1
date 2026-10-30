param([Parameter(Mandatory=$true)][string]$Source,[Parameter(Mandatory=$true)][string]$Chunk)
$ErrorActionPreference='Stop'
$stream=$null
try {
 $path=[IO.Path]::GetFullPath($Source)
 $node=$path
 while($node){$a=[IO.File]::GetAttributes($node);if(([int]$a -band (0x400 -bor 0x1000 -bor 0x40000 -bor 0x400000)) -ne 0){throw 'Source is linked, offline or unavailable.'};$node=[IO.Path]::GetDirectoryName($node)}
 # FileShare.Read permits SFTP/hash readers, but denies writers and deletion.
 $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $sha=[Security.Cryptography.SHA256]::Create()
 $hash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()
 @{sha256=$hash;bytes=$stream.Length}|ConvertTo-Json -Compress|Write-Output
 while($line=[Console]::ReadLine()) {
  $r=$line|ConvertFrom-Json
  if($r.command -eq 'close'){break}
  $offset=[long]$r.offset;$length=[int]$r.length
  if($offset -lt 0 -or $length -le 0 -or $length -gt 67108864 -or ($offset+$length) -gt $stream.Length){throw 'Invalid chunk range.'}
  $null=$stream.Seek($offset,[IO.SeekOrigin]::Begin)
  $out=[IO.File]::Open($Chunk,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try {$buffer=New-Object byte[] 1048576;$left=$length;while($left -gt 0){$n=$stream.Read($buffer,0,[Math]::Min($left,$buffer.Length));if($n -eq 0){throw 'Source ended unexpectedly.'};$out.Write($buffer,0,$n);$left-=$n};$out.Flush($true)} finally {$out.Dispose()}
  $chunkStream=[IO.File]::OpenRead($Chunk);try{$chunkHash=[BitConverter]::ToString($sha.ComputeHash($chunkStream)).Replace('-','').ToLowerInvariant()}finally{$chunkStream.Dispose()};@{sha256=$chunkHash;bytes=$length}|ConvertTo-Json -Compress|Write-Output
 }
} catch {@{error=$_.Exception.Message}|ConvertTo-Json -Compress|Write-Output;exit 1} finally {if($stream){$stream.Dispose()}}

// Audit only the exact earlier echo diagnostic commands; never selects Buddy/MAGIX.
import { spawnSync } from 'node:child_process';
import { loadConfig, buildSshArgs } from '../transport.mjs';
const config = await loadConfig();
const args = buildSshArgs(config);
const prefix = args.slice(0, args.indexOf(config.host) + 1);
const echo = "[Console]::WriteLine('ready');[Console]::Out.Flush();while(($line=[Console]::ReadLine()) -ne $null){[Console]::WriteLine('received:'+ $line);[Console]::Out.Flush()}";
const inner = Buffer.from(echo, 'utf16le').toString('base64');
const outer = Buffer.from("$ProgressPreference='SilentlyContinue'; & 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' '-NoLogo' '-NoProfile' '-NonInteractive' '-EncodedCommand' '" + inner + "'; exit $LASTEXITCODE", 'utf16le').toString('base64');
const script = `$matches = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -and ($_.CommandLine.Contains('${inner}') -or $_.CommandLine.Contains('${outer}')) }); $matches | Select-Object ProcessId,ParentProcessId,CreationDate | ConvertTo-Json -Compress; ${process.argv.includes('--cleanup') ? '$matches | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction Stop };' : ''}`;
const result = spawnSync(config.sshExecutable, [...prefix, 'powershell.exe', '-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')], { windowsHide: true, encoding: 'utf8', timeout: 15000 });
console.log(JSON.stringify({ code: result.status, matches: result.stdout.trim() || '[]', stderr: result.stderr, cleaned: process.argv.includes('--cleanup') }));
if (result.status !== 0) process.exitCode = 1;

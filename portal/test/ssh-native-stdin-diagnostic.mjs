// Explicit bounded native-stdin probe. Does not launch Buddy or MAGIX.
import { spawn, spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadConfig, buildSshArgs } from '../transport.mjs';

const config = await loadConfig();
const args = buildSshArgs(config);
const hostIndex = args.indexOf(config.host);
const prefix = args.slice(0, hostIndex + 1);
const remoteDirectory = 'C:\\Users\\jlewi\\AppData\\Local\\ED-IT Buddy\\portal\\transport-diagnostic-echo';
const remoteExe = remoteDirectory + '\\EchoBridge.exe';
const prepare = `$ErrorActionPreference='Stop';New-Item -ItemType Directory -Path '${remoteDirectory}' -Force | Out-Null; Get-ItemProperty -LiteralPath 'HKLM:\\SOFTWARE\\OpenSSH' -Name DefaultShell -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DefaultShell; exit 0`;
const prepared = spawnSync(config.sshExecutable, [...prefix, 'powershell.exe', '-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(prepare, 'utf16le').toString('base64')], { windowsHide: true, encoding: 'utf8', timeout: 10000 });
if (prepared.status !== 0) throw new Error(prepared.stderr || 'Cannot prepare diagnostic folder.');
console.log(JSON.stringify({ defaultShell: prepared.stdout.trim() || 'OpenSSH default' }));
const source = fileURLToPath(new URL('./EchoBridge.exe', import.meta.url));
const copy = spawnSync(path.join(path.dirname(config.sshExecutable), 'scp.exe'), [...prefix.slice(1, -1), source, config.host + ':' + remoteExe.replaceAll('\\', '/')], { windowsHide: true, encoding: 'utf8', timeout: 10000 });
if (copy.status !== 0) throw new Error(copy.stderr || 'Cannot copy diagnostic helper.');

for (const mode of ['no-encoding', 'raw', 'handle']) {
  const command = [`"${remoteExe}"`, mode];
  const started = Date.now();
  const child = spawn(config.sshExecutable, [...prefix, ...command], { windowsHide: true, stdio: mode.startsWith('overlapped') ? ['overlapped', 'overlapped', 'overlapped'] : ['pipe', 'pipe', 'pipe'] });
  const events = [];
  let stderr = '';
  child.stdout.on('data', chunk => events.push({ ms: Date.now() - started, text: chunk.toString() }));
  child.stderr.on('data', chunk => { stderr += chunk; });
  child.stdin.on('error', () => {});
  child.stdin.write('buddy-stdin-probe-one\r\n');
  const second = setTimeout(() => child.stdin.write('buddy-stdin-probe-two\r\n'), 1500);
  const eof = setTimeout(() => child.stdin.end(), 2500);
  // Native helper self-exits after5s; this is only a final transport timeout.
  const deadline = setTimeout(() => child.kill(), 8000);
  await new Promise(resolve => child.on('close', (code, signal) => {
    clearTimeout(second); clearTimeout(eof); clearTimeout(deadline);
    console.log(JSON.stringify({ mode, code, signal, elapsedMs: Date.now() - started, events, stderr }));
    resolve();
  }));
}

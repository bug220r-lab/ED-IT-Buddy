// Explicit live, read-only diagnostic. Never starts a MAGIX action or job.
import { spawn } from 'node:child_process';
import { writeFile } from 'node:fs/promises';
import { loadConfig, SshTransport } from '../transport.mjs';

const started = Date.now();
const children = [];
const events = [];
const config = await loadConfig();
const transport = new SshTransport(config, { spawnProcess(command, args, options) {
  const child = spawn(command, args, options);
  children.push(child);
  child.on('close', (code, signal) => events.push({ kind: 'ssh_exit', ms: Date.now() - started, code, signal }));
  return child;
} });
const originalRequest = transport.request.bind(transport);
transport.request = async (method, params) => {
  const before = Date.now();
  const result = await originalRequest(method, params);
  events.push({ kind: 'response', method, ms: Date.now() - started, duration_ms: Date.now() - before,
    ...(method === 'get_state' ? { host: result.host, session_id: result.session_id, online: result.online } : {}) });
  return result;
};
let error = null;
try {
  await transport.request('get_state', {});
  await transport.request('get_capabilities', {});
  await new Promise(resolve => setTimeout(resolve, 20000));
  await transport.request('get_state', {});
  await new Promise(resolve => setTimeout(resolve, 20000));
  await transport.request('get_capabilities', {});
  await transport.request('get_state', {});
} catch (failure) { error = failure.toJSON?.() ?? { message: failure.message }; }
finally { await transport.close(); }
const report = { scope: 'read-only SSH transport and heartbeat soak, no editing proof',
  elapsed_ms: Date.now() - started, ssh_process_count: children.length,
  ssh_forced_kill: children.some(child => child.killed), events, error };
report.passed = !error && children.length === 1 && !report.ssh_forced_kill &&
  events.filter(event => event.method === 'ping').length >= 7 &&
  events.some(event => event.kind === 'ssh_exit' && event.code === 0);
if (process.argv[2]) await writeFile(process.argv[2], JSON.stringify(report, null, 2) + '\n', { flag: 'wx' });
console.log(JSON.stringify(report, null, 2));
if (!report.passed) process.exitCode = 1;

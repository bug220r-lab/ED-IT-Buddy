import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { SshTransport, loadConfig, buildSshArgs } from '../transport.mjs';
import { schemas } from '../schemas.mjs';

const config = {
  host: 'jlewi@192.168.247.1', identityFile: 'C:\\Users\\Jude\\.ssh\\edit_buddy_msi_ed25519',
  knownHostsFile: 'C:\\Users\\Jude\\.ssh\\known_hosts', hostKeyAlias: 'msi', sshExecutable: 'ssh.exe',
  remoteBridge: 'C:\\Users\\jlewi\\AppData\\Local\\ED-IT Buddy\\portal\\v0.2.0\\ED-IT Buddy.Bridge.exe',
  requestTimeoutMs: 2000, heartbeatIntervalMs: 5000
};

function fakeBridge(onRequest, { exitOnEof = true } = {}) {
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.requests = [];
  child.killed = false;
  child.kill = () => { child.killed = true; queueMicrotask(() => child.emit('close', 0, null)); return true; };
  let buffer = '';
  child.stdin = new Writable({ write(chunk, encoding, callback) {
    buffer += chunk.toString();
    let newline;
    while ((newline = buffer.indexOf('\n')) >= 0) {
      const request = JSON.parse(buffer.slice(0, newline));
      buffer = buffer.slice(newline + 1);
      child.requests.push(request);
      queueMicrotask(() => onRequest?.(request, child));
    }
    callback();
  } });
  if (exitOnEof) child.stdin.on('finish', () => queueMicrotask(() => child.emit('close', 0, null)));
  child.reply = (request, result, error = null) => child.stdout.write(JSON.stringify({ id: request.id, result, error }) + '\n');
  return child;
}

function makeTransport(child, overrides = {}) {
  return new SshTransport({ ...config, ...overrides }, { spawnProcess: () => child, diagnostic: () => {} });
}

test('SSH uses strict pinned host options and only trusted executable in shell script', async () => {
  const loaded = await loadConfig();
  assert.equal(loaded.host, config.host);
  const args = buildSshArgs(config);
  assert.ok(args.includes('StrictHostKeyChecking=yes'));
  assert.ok(args.includes('BatchMode=yes'));
  assert.ok(args.includes('ForwardAgent=no'));
  assert.ok(args.includes('ClearAllForwardings=yes'));
  assert.equal(args.at(-2), '-EncodedCommand');
  assert.equal(Buffer.from(args.at(-1), 'base64').toString('utf16le'),
    `$ProgressPreference='SilentlyContinue'; & '${config.remoteBridge}'; exit $LASTEXITCODE`);
});

test('correlates out-of-order responses and keeps job text out of shell arguments', async () => {
  const bridge = fakeBridge();
  const transport = makeTransport(bridge);
  const first = transport.request('get_job', { job_id: 'one' });
  const second = transport.request('run_sequence', { request_id: 'two', text: "$(danger); `stuff` ' quoted" });
  bridge.reply(bridge.requests[1], { job_id: 'two' });
  const response = JSON.stringify({ id: bridge.requests[0].id, result: { status: 'completed' }, error: null }) + '\r\n';
  bridge.stdout.write(response.slice(0, 7));
  bridge.stdout.write(response.slice(7));
  assert.deepEqual(await first, { status: 'completed' });
  assert.deepEqual(await second, { job_id: 'two' });
  assert.equal(bridge.requests[1].params.text, "$(danger); `stuff` ' quoted");
  transport.close();
});

test('disconnect after submission is uncertain and a later query reconnects without replay', async () => {
  const bridges = [fakeBridge(), fakeBridge((request, child) => child.reply(request, { status: 'stopped' }))];
  let starts = 0;
  const transport = new SshTransport(config, { spawnProcess: () => bridges[starts++], diagnostic: () => {} });
  const submitted = transport.request('run_sequence', { request_id: 'stable-request', steps: [] });
  bridges[0].emit('close', 255, null);
  await assert.rejects(submitted, error => error.code === 'transport_lost' && error.uncertain);
  const job = await transport.request('get_job', { job_id: 'stable-request' });
  assert.equal(job.status, 'stopped');
  assert.equal(starts, 2);
  assert.deepEqual(bridges.flatMap(child => child.requests.map(request => request.method)), ['run_sequence', 'get_job']);
  transport.close();
});

test('timeout drops lease and preserves uncertainty for edits', async () => {
  const child = fakeBridge();
  const transport = makeTransport(child, { requestTimeoutMs: 25 });
  await assert.rejects(transport.request('run_sequence', { request_id: 'timed' }), error => error.code === 'request_timeout' && error.uncertain);
  assert.equal(child.stdin.writableEnded, true);
  await transport.close();
  assert.equal(child.killed, false, 'EOF should release the remote bridge before killing ssh');
});

test('shutdown sends EOF first and kills only an unresponsive SSH child after grace', async () => {
  const child = fakeBridge((request, bridge) => bridge.reply(request, { ok: true }), { exitOnEof: false });
  const transport = makeTransport(child, { shutdownGraceMs: 30 });
  await transport.request('get_state');
  const closed = transport.close();
  assert.equal(child.stdin.writableEnded, true);
  assert.equal(child.killed, false);
  await closed;
  assert.equal(child.killed, true);
  assert.equal(transport.draining.size, 0);
});

test('graceful shutdown waits for process exit and cancels fallback termination', async () => {
  const child = fakeBridge((request, bridge) => bridge.reply(request, { ok: true }), { exitOnEof: false });
  const transport = makeTransport(child, { shutdownGraceMs: 40 });
  await transport.request('get_state');
  let resolved = false;
  const closed = transport.close().then(() => { resolved = true; });
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(resolved, false);
  child.emit('close', 0, null);
  await closed;
  await new Promise(resolve => setTimeout(resolve, 50));
  assert.equal(child.killed, false);
  assert.equal(transport.draining.size, 0);
});

test('remote rejection remains a rejection and is not retried', async () => {
  const child = fakeBridge((request, bridge) => bridge.reply(request, null, { code: 'unsupported', message: 'No verified trim adapter.' }));
  const transport = makeTransport(child);
  await assert.rejects(transport.request('run_sequence', {}), error => error.code === 'unsupported' && !error.uncertain);
  assert.equal(child.requests.length, 1);
  transport.close();
});

test('non-protocol output fails closed', async () => {
  const child = fakeBridge();
  const transport = makeTransport(child);
  const pending = transport.request('get_state');
  child.stdout.write('Welcome from a shell profile!\n');
  await assert.rejects(pending, error => error.code === 'invalid_response');
  assert.equal(child.stdin.writableEnded, true);
  await transport.close();
});

test('heartbeats keep the bridge alive and stop when transport closes', async () => {
  const child = fakeBridge((request, bridge) => bridge.reply(request, { ok: true }));
  const transport = makeTransport(child, { heartbeatIntervalMs: 20 });
  await transport.request('get_state');
  await new Promise(resolve => setTimeout(resolve, 55));
  assert.ok(child.requests.some(request => request.method === 'ping'));
  transport.close();
  const count = child.requests.length;
  await new Promise(resolve => setTimeout(resolve, 35));
  assert.equal(child.requests.length, count);
});

test('unknown methods never create an SSH process', async () => {
  let starts = 0;
  const transport = new SshTransport(config, { spawnProcess: () => { starts++; }, diagnostic: () => {} });
  await assert.rejects(transport.request('send_keys', { text: 'anything' }), error => error.code === 'unknown_method');
  assert.equal(starts, 0);
  transport.close();
});

test('typed sequence validates inclusive/exclusive frames and disallows arbitrary controls', () => {
  const valid = { request_id: 'demo-001', workspace: 'C:\\Buddy\\demo', steps: [
    { action: 'trim_clip', args: { clip_id: 'a', source_in_frame: 0, source_out_frame: 60 } }
  ] };
  assert.deepEqual(schemas.run_sequence.parse(valid), valid);
  assert.throws(() => schemas.run_sequence.parse({ ...valid, steps: [{ action: 'send_keys', args: { keys: 'CTRL+A' } }] }));
  assert.throws(() => schemas.run_sequence.parse({ ...valid, steps: [{ action: 'trim_clip', args: { clip_id: 'a', source_in_frame: 60, source_out_frame: 30 } }] }));
  assert.throws(() => schemas.run_sequence.parse({ ...valid, arbitrary_script: 'oops' }));
});

test('public action bounds match core and the MAGIX worker', () => {
  const sequence = step => ({ request_id: 'schema.001', workspace: 'C:\\Buddy\\demo', steps: [step] });
  const create = { action: 'create_project', args: { project_path: 'C:\\Buddy\\demo\\test.MVP', width: 7680, height: 4320, fps: 240 } };
  assert.ok(schemas.run_sequence.safeParse(sequence(create)).success);
  for (const [field, value] of [['width', 7681], ['height', 4321], ['fps', 241], ['project_path', 'C:\\' + 'a'.repeat(238)]]) {
    assert.equal(schemas.run_sequence.safeParse(sequence({ ...create, args: { ...create.args, [field]: value } })).success, false, field);
  }
  const title = { action: 'add_title', args: { text: 'a'.repeat(500), timeline_frame: 100000000, duration_frames: 100000000, track: 99 } };
  assert.ok(schemas.run_sequence.safeParse(sequence(title)).success);
  for (const [field, value] of [['text', 'a'.repeat(501)], ['timeline_frame', 100000001], ['duration_frames', 100000001], ['track', 100]]) {
    assert.equal(schemas.run_sequence.safeParse(sequence({ ...title, args: { ...title.args, [field]: value } })).success, false, field);
  }
  for (const clip_id of ['a'.repeat(65), 'clip.with.dot', 'clip space']) {
    assert.equal(schemas.run_sequence.safeParse(sequence({ action: 'append_clip', args: { clip_id, track: 1 } })).success, false, clip_id);
  }
  assert.ok(schemas.run_sequence.safeParse(sequence({ action: 'append_clip', args: { clip_id: 'a_-'.repeat(21), track: 1 } })).success);
});

test('official MCP server initializes and advertises exactly the five portal tools', { timeout: 10000 }, async () => {
  const child = spawn(process.execPath, [fileURLToPath(new URL('../server.mjs', import.meta.url))], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = createInterface({ input: child.stdout });
  const responses = new Map();
  let stderr = '';
  child.stderr.on('data', text => { stderr += text; });
  lines.on('line', line => {
    const result = JSON.parse(line);
    if (responses.has(result.id)) { responses.get(result.id)(result); responses.delete(result.id); }
  });
  const call = (id, method, params) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`MCP timeout ${method}: ${stderr}`)), 5000);
    responses.set(id, value => { clearTimeout(timer); resolve(value); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  try {
    const init = await call(1, 'initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'buddy-test', version: '1' } });
    assert.equal(init.result.serverInfo.name, 'edit-buddy');
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
    const list = await call(2, 'tools/list', {});
    assert.deepEqual(list.result.tools.map(tool => tool.name).sort(), ['get_capabilities', 'get_job', 'get_state', 'run_sequence', 'stop_job']);
    const rejected = await call(3, 'tools/call', { name: 'run_sequence', arguments: { steps: [] } });
    assert.ok(rejected.error || rejected.result?.isError);
    assert.equal(stderr.includes('[MSI bridge]'), false, 'invalid parameters must not initiate SSH');
  } finally { child.stdin.end(); child.kill(); lines.close(); }
});

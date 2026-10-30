import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';

export const METHODS = new Set(['get_state', 'get_capabilities', 'run_sequence', 'get_job', 'stop_job', 'ping']);
const MUTATIONS = new Set(['run_sequence', 'stop_job']);
const MAX_LINE_BYTES = 1024 * 1024;

export class PortalError extends Error {
  constructor(code, message, uncertain = false) {
    super(message);
    this.name = 'PortalError';
    this.code = code;
    this.uncertain = uncertain;
  }
  toJSON() { return { code: this.code, message: this.message, uncertain: this.uncertain }; }
}

export async function loadConfig(path = process.env.EDIT_BUDDY_PORTAL_CONFIG) {
  const configPath = path || fileURLToPath(new URL('./config.example.json', import.meta.url));
  const value = JSON.parse((await readFile(configPath, 'utf8')).replace(/^\uFEFF/, ''));
  for (const key of ['host', 'identityFile', 'knownHostsFile', 'hostKeyAlias', 'sshExecutable', 'remoteBridge']) {
    if (typeof value[key] !== 'string' || !value[key] || /[\r\n\0]/.test(value[key])) {
      throw new PortalError('invalid_configuration', `Invalid portal configuration field: ${key}`);
    }
  }
  if (!/^[A-Za-z0-9_.-]+@[A-Za-z0-9_.:-]+$/.test(value.host)) {
    throw new PortalError('invalid_configuration', 'SSH host must be an explicit user@host.');
  }
  if (!/^[A-Za-z]:\\.*\.exe$/i.test(value.remoteBridge)) {
    throw new PortalError('invalid_configuration', 'remoteBridge must be an absolute Windows executable path.');
  }
  value.requestTimeoutMs ??= 20000;
  value.heartbeatIntervalMs ??= 5000;
  value.shutdownGraceMs ??= 2000;
  for (const key of ['requestTimeoutMs', 'heartbeatIntervalMs']) {
    if (!Number.isInteger(value[key]) || value[key] < 100 || value[key] > 60000) {
      throw new PortalError('invalid_configuration', `Invalid timeout: ${key}`);
    }
  }
  if (!Number.isInteger(value.shutdownGraceMs) || value.shutdownGraceMs < 100 || value.shutdownGraceMs > 5000) {
    throw new PortalError('invalid_configuration', 'Invalid timeout: shutdownGraceMs');
  }
  return value;
}

export function buildSshArgs(config) {
  // Only this trusted deployment path enters the shell command. Job data is stdin JSON.
  const bridgeLiteral = `'${config.remoteBridge.replaceAll("'", "''")}'`;
  const script = `$ProgressPreference='SilentlyContinue'; & ${bridgeLiteral}; exit $LASTEXITCODE`;
  const encoded = Buffer.from(script, 'utf16le').toString('base64');
  return ['-T', '-i', config.identityFile,
    '-o', `UserKnownHostsFile=${config.knownHostsFile.replaceAll('\\', '/')}`,
    '-o', `HostKeyAlias=${config.hostKeyAlias}`,
    '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
    '-o', 'ForwardAgent=no', '-o', 'ClearAllForwardings=yes',
    '-o', 'ConnectTimeout=10', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=4',
    config.host, 'powershell.exe', '-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded];
}

export class SshTransport {
  constructor(config, { spawnProcess = spawn, diagnostic = text => process.stderr.write(text) } = {}) {
    this.config = config;
    this.spawnProcess = spawnProcess;
    this.diagnostic = diagnostic;
    this.connection = null;
    this.closed = false;
    this.pending = new Map();
    this.heartbeat = null;
    this.pinging = false;
    this.draining = new Set();
  }

  connect() {
    if (this.closed) throw new PortalError('closed', 'Portal transport has been closed.');
    if (this.connection) return this.connection;
    const child = this.spawnProcess(this.config.sshExecutable, buildSshArgs(this.config), {
      shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']
    });
    const connection = { child, buffer: '', finished: false, exited: false };
    this.connection = connection;
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => this.receive(connection, chunk));
    child.stderr.on('data', text => this.diagnostic(`[MSI bridge] ${text.slice(0, 4096)}`));
    child.stdin.on('error', error => this.fail(connection, 'transport_lost', error.message));
    child.on('error', error => this.fail(connection, 'ssh_start_failed', error.message));
    child.on('close', (code, signal) => {
      connection.exited = true;
      connection.finishShutdown?.();
      this.fail(connection, 'transport_lost',
        `MSI bridge connection closed (${signal || code}). Query the job before submitting another edit.`);
    });
    this.heartbeat = setInterval(async () => {
      if (this.pinging || this.connection !== connection) return;
      this.pinging = true;
      try { await this.request('ping', {}); }
      catch (error) { this.diagnostic(`[Buddy heartbeat] ${error.message}\n`); }
      finally { this.pinging = false; }
    }, this.config.heartbeatIntervalMs);
    this.heartbeat.unref();
    return connection;
  }

  receive(connection, chunk) {
    if (connection.finished) return;
    connection.buffer += chunk;
    if (Buffer.byteLength(connection.buffer, 'utf8') > MAX_LINE_BYTES) {
      this.fail(connection, 'invalid_response', 'MSI bridge response exceeded the size limit.');
      return;
    }
    let newline;
    while ((newline = connection.buffer.indexOf('\n')) !== -1) {
      const line = connection.buffer.slice(0, newline).replace(/\r$/, '').replace(/^\uFEFF/, '');
      connection.buffer = connection.buffer.slice(newline + 1);
      if (!line) continue;
      let message;
      try { message = JSON.parse(line); }
      catch { this.fail(connection, 'invalid_response', 'MSI bridge emitted non-JSON output.'); return; }
      if (!message || typeof message.id !== 'string' || (!('result' in message) && !('error' in message))) {
        this.fail(connection, 'invalid_response', 'MSI bridge emitted an invalid response envelope.'); return;
      }
      const pending = this.pending.get(message.id);
      if (!pending || pending.connection !== connection) continue;
      this.pending.delete(message.id);
      clearTimeout(pending.timer);
      if (message.error) {
        pending.reject(new PortalError(message.error.code || 'remote_error',
          message.error.message || 'MSI rejected the request.', Boolean(message.error.uncertain)));
      } else pending.resolve(message.result);
    }
  }

  fail(connection, code, message) {
    if (connection.finished) return;
    connection.finished = true;
    if (this.connection === connection) {
      this.connection = null;
      clearInterval(this.heartbeat);
      this.heartbeat = null;
    }
    for (const [id, pending] of this.pending) {
      if (pending.connection !== connection) continue;
      this.pending.delete(id);
      clearTimeout(pending.timer);
      pending.reject(new PortalError(code, message, MUTATIONS.has(pending.method)));
    }
    this.shutdown(connection);
  }

  shutdown(connection) {
    if (connection.exited || connection.shutdown) return;
    // EOF lets the remote bridge release its named pipe and lease before SSH exits.
    // Killing ssh immediately can leave a remote Windows child waiting for stdin.
    let complete;
    connection.shutdown = new Promise(resolve => { complete = resolve; });
    this.draining.add(connection.shutdown);
    connection.finishShutdown = () => {
      clearTimeout(connection.shutdownTimer);
      this.draining.delete(connection.shutdown);
      complete();
    };
    connection.shutdownTimer = setTimeout(() => {
      connection.child.stdin.destroy();
      if (!connection.exited) connection.child.kill();
      connection.finishShutdown();
    }, this.config.shutdownGraceMs ?? 2000);
    // Only the SSH process is a fallback termination target. Never terminate MAGIX.
    connection.child.stdin.end();
  }

  async request(method, params = {}) {
    if (!METHODS.has(method)) throw new PortalError('unknown_method', 'Unsupported portal method.');
    if (!params || typeof params !== 'object' || Array.isArray(params)) {
      throw new PortalError('invalid_params', 'Portal parameters must be an object.');
    }
    const id = randomUUID();
    const wire = JSON.stringify({ id, method, params }) + '\n';
    if (Buffer.byteLength(wire, 'utf8') > MAX_LINE_BYTES) throw new PortalError('request_too_large', 'Portal request is too large.');
    const connection = this.connect();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        // A timed-out mutation is uncertain. Dropping the lease prevents later queued steps.
        this.fail(connection, 'request_timeout', 'MSI did not answer before the deadline. Query the job; do not replay edits.');
      }, this.config.requestTimeoutMs);
      this.pending.set(id, { resolve, reject, timer, connection, method });
      connection.child.stdin.write(wire, 'utf8', error => {
        if (error) this.fail(connection, 'transport_lost', error.message);
      });
    });
  }

  close() {
    this.closed = true;
    clearInterval(this.heartbeat);
    if (this.connection) this.fail(this.connection, 'closed', 'Portal transport closed.');
    return Promise.all([...this.draining]);
  }
}

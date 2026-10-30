import { createInterface } from 'node:readline';
import { loadConfig, SshTransport, PortalError, METHODS } from './transport.mjs';
import { schemas } from './schemas.mjs';

const args = process.argv.slice(2);
const option = name => {
  const index = args.indexOf(name);
  return index < 0 ? undefined : args[index + 1];
};
const transport = new SshTransport(await loadConfig(option('--config')));
const asError = error => error instanceof PortalError ? error.toJSON() : { code: 'invalid_request', message: error.message };
const send = message => process.stdout.write(JSON.stringify(message) + '\n');
process.on('SIGINT', async () => { await transport.close(); process.exit(0); });
process.on('SIGTERM', async () => { await transport.close(); process.exit(0); });

async function dispatch(request) {
  const id = request?.id;
  try {
    if (typeof id !== 'string' || !id || id.length > 100 || !METHODS.has(request.method)) {
      throw new PortalError('invalid_request', 'Expected id, supported method and params.');
    }
    const params = request.method === 'ping' ? {} : schemas[request.method].parse(request.params ?? {});
    return { id, result: await transport.request(request.method, params), error: null };
  } catch (error) { return { id: typeof id === 'string' ? id : null, result: null, error: asError(error) }; }
}

if (args.includes('--rpc')) {
  // The GUI holds this process open. EOF drops its remote connection/lease.
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  const pending = new Set();
  lines.on('line', line => {
    if (Buffer.byteLength(line, 'utf8') > 1024 * 1024) {
      send({ id: null, result: null, error: { code: 'request_too_large', message: 'Request exceeded the size limit.' } });
      return;
    }
    let request;
    try { request = JSON.parse(line.replace(/^\uFEFF/, '')); }
    catch { send({ id: null, result: null, error: { code: 'invalid_json', message: 'Expected one JSON request per line.' } }); return; }
    const promise = dispatch(request).then(send).finally(() => pending.delete(promise));
    pending.add(promise);
  });
  lines.on('close', async () => {
    // GUI disappearance immediately revokes its lease, even during a slow request.
    const closed = transport.close();
    await Promise.allSettled([...pending]);
    await closed;
  });
} else {
  const method = option('--method') || 'get_state';
  try {
    if (method === 'run_sequence' && !args.includes('--wait')) {
      throw new PortalError('requires_persistent_connection', 'Use --rpc or --wait to keep the job lease alive.');
    }
    const params = JSON.parse(option('--params') || '{}');
    const response = await dispatch({ id: 'cli', method, params });
    send(response);
    if (response.error) process.exitCode = 1;
    if (method === 'run_sequence' && args.includes('--wait') && !response.error) {
      const jobId = response.result.job_id;
      if (!jobId) throw new PortalError('invalid_response', 'Submission did not return a job_id.');
      const terminal = new Set(['completed', 'failed', 'stopped', 'blocked', 'unsupported', 'uncertain', 'cancelled', 'paused', 'interrupted']);
      for (;;) {
        const job = await transport.request('get_job', { job_id: jobId });
        send({ id: 'cli', result: job, error: null });
        if (terminal.has(job.status)) break;
        await new Promise(resolve => setTimeout(resolve, 1000));
      }
    }
  } catch (error) { send({ id: 'cli', result: null, error: asError(error) }); process.exitCode = 1; }
  finally { await transport.close(); }
}


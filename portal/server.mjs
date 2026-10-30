import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { loadConfig, SshTransport, PortalError } from './transport.mjs';
import { schemas, descriptions } from './schemas.mjs';

const transport = new SshTransport(await loadConfig());
const instructions = 'ED-IT Buddy operates MAGIX on MSI, never THIS-MACHINE. Read get_state and get_capabilities before edits. Submit only requested operations in the dedicated test workspace. A reported timeout after input is uncertain: query get_job, never blindly replay. Stop prevents further steps and is not Undo. A successful command is not proof of a verified export.';

serveStdio(() => {
  const server = new McpServer({ name: 'edit-buddy', version: '0.2.0' }, { instructions });
  for (const [name, inputSchema] of Object.entries(schemas)) {
    server.registerTool(name, {
      description: descriptions[name], inputSchema,
      annotations: {
        readOnlyHint: name.startsWith('get_'),
        destructiveHint: name === 'run_sequence',
        idempotentHint: true,
        openWorldHint: false
      }
    }, async params => {
      try {
        const result = await transport.request(name, params);
        return { content: [{ type: 'text', text: JSON.stringify(result) }], structuredContent: { result } };
      } catch (error) {
        const detail = error instanceof PortalError ? error.toJSON() : { code: 'adapter_error', message: error.message };
        return { isError: true, content: [{ type: 'text', text: JSON.stringify(detail) }], structuredContent: { error: detail } };
      }
    });
  }
  return server;
});

process.stdin.on('end', () => transport.close());
process.on('SIGINT', async () => { await transport.close(); process.exit(0); });
process.on('SIGTERM', async () => { await transport.close(); process.exit(0); });

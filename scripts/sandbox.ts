import fs from 'node:fs/promises';
import path from 'node:path';
import { createSandboxServer } from '../src/sandbox/server.ts';

const { server, token } = createSandboxServer(741, process.env.SANDBOX_SESSION_TOKEN);
const port = Number(process.env.SANDBOX_PORT || 3221);
server.listen(port, '127.0.0.1', async () => {
  const address = server.address();
  if (!address || typeof address === 'string') return;
  const origin = `http://127.0.0.1:${address.port}`;
  const sessionFile = path.resolve('.tmp/sandbox-session.json');
  await fs.mkdir(path.dirname(sessionFile), { recursive: true });
  await fs.writeFile(sessionFile, JSON.stringify({ origin, token, pid: process.pid }, null, 2), { mode: 0o600 });
  console.log(`Sandbox: ${origin}/#token=${token}`);
  console.log(`Local API session: ${sessionFile}`);
  console.log('Simulation only. No automatic cloud requests; stop with Ctrl+C.');
});
server.on('error', error => { console.error(error.message); process.exitCode = 1; });

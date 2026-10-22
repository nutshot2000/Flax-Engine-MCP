// Copies the plugin into FLAX_PROJECT, hot-reloads it in the open editor and waits until the new bridge answers.
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { connect } from '../tests/client.js';
import { project } from '../src/bridge.js';
execFileSync(process.execPath, [fileURLToPath(new URL('../scripts/sync-plugin.js', import.meta.url)), project], { stdio: 'inherit' });
const { client, call } = await connect();
try {
  const before = (await call('status')).compilationCount;
  await call('compile');
  const end = Date.now() + 180000;
  for (;;) {
    if (Date.now() > end) throw Error('Timed out waiting for recompilation');
    await new Promise(r => setTimeout(r, 1000));
    const s = await call('status').catch(() => null);
    if (s && !s.compiling && s.compilationCount > before) {
      if (s.compilationFailed) throw Error('Plugin compilation failed; read flax_logs filter "error CS"');
      console.log(`Bridge ${s.version} reloaded (compilation #${s.compilationCount})`);
      break;
    }
  }
} finally { await client.close(); }

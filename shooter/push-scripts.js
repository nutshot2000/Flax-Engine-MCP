// Writes shooter/Source/*.cs into the project through flax_write_script, compiles once and reports C# errors.
// Usage: FLAX_PROJECT=D:/FlaxProjects/BridgeGame node shooter/push-scripts.js
import { promises as fs } from 'node:fs';
import { connect } from '../tests/client.js';
const { client, call } = await connect();
const dir = new URL('./Source/', import.meta.url);
try {
  const before = (await call('status')).compilationCount;
  for (const file of (await fs.readdir(dir)).filter(f => f.endsWith('.cs'))) {
    const r = await client.callTool({ name: 'flax_write_script', arguments: { path: 'Shooter/' + file, source: await fs.readFile(new URL(file, dir), 'utf8'), overwrite: true, compile: false } });
    if (r.isError) throw Error(r.content[0].text);
    console.log('Wrote', file);
  }
  await call('compile');
  let status;
  const end = Date.now() + 180000;
  do { await new Promise(r => setTimeout(r, 1000)); status = await call('status').catch(() => null); }
  while (Date.now() < end && !(status && !status.compiling && status.compilationCount > before));
  if (!status || status.compilationCount <= before) throw Error('Timed out waiting for compilation');
  if (status.compilationFailed) {
    const log = await client.callTool({ name: 'flax_logs', arguments: { filter: 'error CS', lines: 40 } });
    console.log(JSON.parse(log.content[0].text).text);
    process.exitCode = 1;
  } else console.log(`Compiled (#${status.compilationCount})`);
} finally { await client.close(); }

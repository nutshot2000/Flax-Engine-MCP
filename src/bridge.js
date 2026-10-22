import { promises as fs } from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
export const project = path.resolve(process.env.FLAX_PROJECT || fileURLToPath(new URL('../examples/NeonRelay',import.meta.url)));
export const queue = path.join(project, 'Cache', 'McpBridge');
export const delay = ms => new Promise(r => setTimeout(r, ms));
export async function readJson(file) { return JSON.parse(await fs.readFile(file, 'utf8')); }
export async function receipt(id) {
  if (!/^[0-9a-f-]{36}$/.test(id)) throw Error('Invalid operation ID');
  return readJson(path.join(queue, 'receipts', id + '.json'));
}
export async function request(op, args = {}, timeout = 20000) {
  const status = await readJson(path.join(queue, 'status.json')).catch(() => null);
  if (!status || Date.now() - Date.parse(status.utc) > 10000) throw Error('Editor bridge is offline. Open the configured project and check compilation logs.');
  const id = randomUUID();
  const file = path.join(queue, 'requests', id + '.json');
  await fs.writeFile(file + '.tmp', JSON.stringify({ id, op, args, deadline: new Date(Date.now() + timeout).toISOString() }));
  await fs.rename(file + '.tmp', file);
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    const result = await receipt(id).catch(() => null);
    if (result?.state === 'complete') return { operationId: id, ...result };
    if (result?.state === 'failed') throw Error(JSON.stringify(result));
    await delay(100);
  }
  throw Error(`Outcome unknown. Inspect flax_receipt with operationId ${id}; do not replay the mutation.`);
}
export function sourcePath(relative) {
  const base = path.join(project, 'Source', 'Game');
  const full = path.resolve(base, relative);
  if (!full.startsWith(base + path.sep) || !full.endsWith('.cs') || full.endsWith('.Build.cs')) throw Error('Expected a .cs file beneath Source/Game');
  return full;
}

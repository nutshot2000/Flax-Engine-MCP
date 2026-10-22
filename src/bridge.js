import { promises as fs, existsSync, readFileSync } from 'node:fs';
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
// The editor processes requests in ordinal file-name order; a timestamp plus per-process sequence keeps parallel tool calls in submission order.
let sequence = 0;
export function requestName(id, now = Date.now()) {
  return `${String(now).padStart(15, '0')}-${String(sequence++ % 1e6).padStart(6, '0')}-${id}.json`;
}
// Request files are published one at a time so the editor never sees a later request before an earlier one.
let publishing = Promise.resolve();
export async function request(op, args = {}, timeout = 20000) {
  const id = randomUUID();
  const file = path.join(queue, 'requests', requestName(id));
  const publish = publishing.then(async () => {
    const status = await readJson(path.join(queue, 'status.json')).catch(() => null);
    if (!status || Date.now() - Date.parse(status.utc) > 10000) throw Error('Editor bridge is offline. Open the configured project and check compilation logs.');
    await fs.writeFile(file + '.tmp', JSON.stringify({ id, op, args, deadline: new Date(Date.now() + timeout).toISOString() }));
    await fs.rename(file + '.tmp', file);
  });
  publishing = publish.catch(() => {});
  await publish;
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    const result = await receipt(id).catch(() => null);
    if (result?.state === 'complete') return { operationId: id, ...result };
    if (result?.state === 'failed') throw Error(result.error ?? JSON.stringify(result));
    await delay(50);
  }
  throw Error(`Outcome unknown. Inspect flax_receipt with operationId ${id}; do not replay the mutation.`);
}
export function sourcePath(relative) {
  const base = path.join(project, 'Source', 'Game');
  const full = path.resolve(base, relative);
  if (!full.startsWith(base + path.sep) || !full.endsWith('.cs') || full.endsWith('.Build.cs')) throw Error('Expected a .cs file beneath Source/Game');
  return full;
}
// FLAX_EDITOR, then FLAX_ENGINE, then engines registered with the Flax Launcher.
export function editorPath(env = process.env) {
  const exe = engine => path.join(engine, 'Binaries', 'Editor', 'Win64', 'Release', 'FlaxEditor.exe');
  if (env.FLAX_EDITOR) return env.FLAX_EDITOR;
  if (env.FLAX_ENGINE) return exe(env.FLAX_ENGINE);
  const versions = path.join(env.APPDATA ?? '', 'Flax', 'Versions.txt');
  if (existsSync(versions)) {
    const bytes = readFileSync(versions);
    const text = bytes.toString(bytes[0] === 0xff && bytes[1] === 0xfe ? 'utf16le' : 'utf8');
    const found = text.split(/\r?\n/).map(l => l.trim().replace(/^﻿/, '')).filter(Boolean).map(exe).find(existsSync);
    if (found) return found;
  }
  throw Error('Flax editor not found. Set FLAX_EDITOR to FlaxEditor.exe or FLAX_ENGINE to the engine folder.');
}

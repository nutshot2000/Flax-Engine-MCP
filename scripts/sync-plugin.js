// Copies plugin/FlaxMcp into the bundled example and, optionally, into another project (FLAX_PROJECT or the first argument).
// An open editor hot-reloads the plugin after the copy.
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.join(root, 'plugin', 'FlaxMcp');
const targets = [path.join(root, 'examples', 'NeonRelay', 'Source', 'FlaxMcp')];
const extra = process.argv[2] ?? process.env.FLAX_PROJECT;
if (extra) targets.push(path.join(path.resolve(extra), 'Source', 'FlaxMcp'));
for (const target of targets) {
  await fs.mkdir(target, { recursive: true });
  for (const file of await fs.readdir(source)) await fs.copyFile(path.join(source, file), path.join(target, file));
  console.log('Synced', target);
}

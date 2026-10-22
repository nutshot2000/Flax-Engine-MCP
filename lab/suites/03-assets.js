import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
export const area = 'assets';
const red = { $type: 'FlaxEngine.Color', value: { R: 1, G: 0, B: 0, A: 1 } };
export const tests = [
  { name: 'engine asset via $engine prefix', run: async ({ call }) => {
    const cube = await call('spawn', { type: 'FlaxEngine.StaticModel', name: 'EngineCube', properties: { Model: '$engine/Editor/Primitives/Cube.flax' } });
    const r = await call('get', { target: { kind: 'actor', id: cube.id }, members: ['Model'] });
    assert.match(r.Model.value.asset, /Cube\.flax$/);
  } },
  { name: 'create material instance and assign by relative path', run: async ({ call }) => {
    await call('material_create', { path: 'Lab/Materials/Red.flax', parameters: { Color: red } });
    const cube = await call('spawn', { type: 'FlaxEngine.StaticModel', name: 'RedCube', properties: { Model: '$engine/Editor/Primitives/Cube.flax' } });
    await call('material', { id: cube.id, path: 'Lab/Materials/Red.flax' });
    const r = await call('call', { target: { kind: 'actor', id: cube.id }, method: 'GetMaterial', args: [0] });
    assert.match(r.value.asset, /Red\.flax$/);
  } },
  { name: 'unknown material parameter lists available ones', run: async ({ call }) => {
    await assert.rejects(call('material_create', { path: 'Lab/Materials/Bad.flax', parameters: { Colour: red } }), /available: .*Color/);
  } },
  { name: 'wrong asset type is rejected', run: async ({ call }) => {
    await assert.rejects(call('spawn', { type: 'FlaxEngine.StaticModel', name: 'BadModel', properties: { Model: 'Lab/Materials/Red.flax' } }), /expected Model/);
  } },
  { name: 'prefab create and spawn', run: async ({ call }) => {
    const root = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'PrefabRoot' });
    await call('spawn', { type: 'FlaxEngine.PointLight', name: 'PrefabLight', parent: root.id });
    await call('prefab_create', { id: root.id, path: 'Lab/Prefabs/Lamp.prefab' });
    const spawned = await call('prefab_spawn', { path: 'Lab/Prefabs/Lamp.prefab', properties: { Name: 'LampInstance' } });
    assert.equal(spawned.name, 'LampInstance'); assert.equal(spawned.children[0].name, 'PrefabLight');
  } },
  { name: 'json asset write and engine load', run: async ({ call, client, until }) => {
    await client.callTool({ name: 'flax_json_asset', arguments: { path: 'Lab/Settings/LabPhysics.json', type: 'FlaxEditor.Content.Settings.PhysicsSettings', data: { DefaultGravity: { X: 0, Y: -500, Z: 0 } } } });
    const gravity = await until(async () => {
      const r = await call('exec', { code: 'var a = Content.Load<JsonAsset>(Path.Combine(Globals.ProjectContentFolder, "Lab/Settings/LabPhysics.json")); return a == null ? null : ((FlaxEditor.Content.Settings.PhysicsSettings)a.Instance).DefaultGravity.Y;' }).catch(() => null);
      return r?.value ?? null;
    }, { what: 'json asset to load' });
    assert.equal(gravity, -500);
  } },
  { name: 'import audio file', run: async ({ call, project, until }) => {
    const source = fileURLToPath(new URL('../../game/audio/collect.wav', import.meta.url));
    await call('import', { source, folder: 'Lab/Audio' });
    await until(async () => existsSync(path.join(project, 'Content', 'Lab', 'Audio', 'collect.flax')), { timeout: 30000, what: 'imported asset' });
    const clip = await until(async () => (await call('exec', { code: 'ctx.Asset<AudioClip>("Lab/Audio/collect.flax").Length' }).catch(() => null))?.value, { what: 'clip load' });
    assert(clip > 0);
    return `clip ${clip.toFixed(2)} s`;
  } },
];

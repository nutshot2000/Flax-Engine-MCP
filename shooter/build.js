// Builds the shooter arena, player, enemy prefab, materials, sounds and navmesh through the MCP bridge.
// Usage: FLAX_PROJECT=D:/FlaxProjects/BridgeGame node shooter/build.js [--rebuild]
// Expects Main.scene open (Sky, Sun, Ambient, Ground, GroundCollider) and the shooter scripts compiled (push-scripts.js).
import { promises as fs } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { connect } from '../tests/client.js';

const { client, call } = await connect();
const V = (X, Y, Z) => ({ X, Y, Z });
const color = (R, G, B) => ({ $type: 'FlaxEngine.Color', value: { R, G, B, A: 1 } });
const wait = ms => new Promise(r => setTimeout(r, ms));
const owned = ['Arena', 'Player', 'SpawnPoints', 'GameDirector', 'NavBounds', 'MainCamera'];

async function materials() {
  const existing = JSON.parse((await client.callTool({ name: 'flax_assets', arguments: { filter: 'Materials' } })).content[0].text).files.join('\n');
  const palette = { Concrete: [0.32, 0.33, 0.35], Wall: [0.16, 0.17, 0.2], Crate: [0.42, 0.29, 0.15], Accent: [1, 0.5, 0.1], DroneBody: [0.08, 0.09, 0.11], DroneEye: [1, 0.08, 0.06], Tracer: [1, 0.85, 0.45], EnemyTracer: [1, 0.12, 0.08], Impact: [0.95, 0.92, 0.8] };
  for (const [name, rgb] of Object.entries(palette))
    if (!existing.includes(name + '.flax')) await call('material_create', { path: `Materials/${name}.flax`, parameters: { Color: color(...rgb) } });
}

// Small synthesized effects: tone sweeps plus filtered noise for gunshots.
async function sounds() {
  const dir = new URL('./audio/', import.meta.url);
  await fs.mkdir(dir, { recursive: true });
  const spec = {
    rifle: { dur: 0.16, noise: 0.9, start: 180, end: 60, decay: 3 }, enemyshot: { dur: 0.22, noise: 0.5, start: 520, end: 140, decay: 2 },
    hit: { dur: 0.07, noise: 0, start: 1800, end: 1500, decay: 1 }, kill: { dur: 0.35, noise: 0.35, start: 600, end: 90, decay: 1.5 },
    hurt: { dur: 0.25, noise: 0.3, start: 160, end: 70, decay: 1.2 }, reload: { dur: 0.3, noise: 0.7, start: 900, end: 700, decay: 6, clicks: true },
    wave: { dur: 0.7, noise: 0, start: 330, end: 880, decay: 0.6 }, jump: { dur: 0.12, noise: 0, start: 260, end: 420, decay: 1.5 },
    lose: { dur: 0.9, noise: 0.1, start: 300, end: 60, decay: 0.8 },
  };
  const existing = JSON.parse((await client.callTool({ name: 'flax_assets', arguments: { filter: 'Audio' } })).content[0].text).files.join('\n');
  for (const [name, s] of Object.entries(spec)) {
    if (existing.includes(name + '.flax')) continue;
    const rate = 44100, n = Math.floor(rate * s.dur), wav = Buffer.alloc(44 + n * 2);
    wav.write('RIFF'); wav.writeUInt32LE(36 + n * 2, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
    wav.writeUInt32LE(rate, 24); wav.writeUInt32LE(rate * 2, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(n * 2, 40);
    let phase = 0, low = 0;
    for (let i = 0; i < n; i++) {
      const t = i / n;
      phase += 2 * Math.PI * (s.start + (s.end - s.start) * t) / rate;
      low += 0.25 * ((Math.random() * 2 - 1) - low);
      let env = Math.min(1, t * 60) * Math.pow(1 - t, s.decay);
      if (s.clicks) env *= (t < 0.15 || (t > 0.6 && t < 0.75)) ? 1 : 0.05;
      const sample = (1 - s.noise) * Math.sin(phase) + s.noise * low * 2.5;
      wav.writeInt16LE(Math.max(-32767, Math.min(32767, Math.floor(sample * env * 14000))), 44 + i * 2);
    }
    const file = new URL(name + '.wav', dir);
    await fs.writeFile(file, wav);
    await call('import', { source: fileURLToPath(file), folder: 'Audio' });
  }
}

// A solid block: scaled cube model with a unit BoxCollider child (inherits the scale).
async function block(name, position, size, material, parent) {
  const model = await call('spawn', { type: 'FlaxEngine.StaticModel', name, parent, properties: { Position: V(...position), Scale: V(size[0] / 100, size[1] / 100, size[2] / 100), Model: '$engine/Editor/Primitives/Cube.flax' } });
  await call('material', { id: model.id, path: `Materials/${material}.flax` });
  await call('spawn', { type: 'FlaxEngine.BoxCollider', name: name + 'Collider', parent: model.id, properties: { Size: V(100, 100, 100) } });
  return model;
}

async function arena() {
  const root = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'Arena' });
  for (const [x, z, w, d] of [[0, 2050, 4200, 100], [0, -2050, 4200, 100], [2050, 0, 100, 4000], [-2050, 0, 100, 4000]]) await block('Wall', [x, 200, z], [w, 400, d], 'Wall', root.id);
  for (const [x, z] of [[-900, -500], [900, -500], [-900, 700], [900, 700], [0, 1300], [0, -200]]) await block('Pillar', [x, 200, z], [200, 400, 200], 'Wall', root.id);
  for (const [x, z, r] of [[-400, -900, 0], [450, -950, 0], [-1400, 100, 1], [1400, 100, 1], [-500, 300, 0], [600, 1000, 0]])
    await block('LowWall', [x, 60, z], r ? [60, 120, 400] : [400, 120, 60], 'Accent', root.id);
  for (const [x, z, s] of [[-1500, -1300, 150], [-1350, -1300, 120], [1500, -1300, 150], [1500, 1400, 150], [-1500, 1450, 150], [300, -400, 120], [-250, 1000, 120], [1100, 300, 150]])
    await block('Crate', [x, s / 2, z], [s, s, s], 'Crate', root.id);
}

async function player() {
  const body = await call('spawn', { type: 'FlaxEngine.CharacterController', name: 'Player', properties: { Position: V(0, 120, -1500), Radius: 35, Height: 110 } });
  const camera = await call('spawn', { type: 'FlaxEngine.Camera', name: 'PlayerCamera', parent: body.id, properties: { LocalPosition: V(0, 60, 0), FieldOfView: 75 } });
  const gun = await call('spawn', { type: 'FlaxEngine.StaticModel', name: 'Rifle', parent: camera.id, properties: { LocalPosition: V(7, -6.5, 20), Scale: V(0.018, 0.024, 0.16), Model: '$engine/Editor/Primitives/Cube.flax' } });
  await call('material', { id: gun.id, path: 'Materials/DroneBody.flax' });
  await call('add_script', { id: body.id, type: 'Game.Health', properties: { Max: 100 } });
  await call('add_script', { id: body.id, type: 'Game.PlayerController', properties: { View: camera.id } });
  return body;
}

async function dronePrefab() {
  const existing = JSON.parse((await client.callTool({ name: 'flax_assets', arguments: { filter: 'Drone.prefab' } })).content[0].text).files;
  if (existing.length) return;
  const drone = await call('spawn', { type: 'FlaxEngine.CharacterController', name: 'Drone', properties: { Position: V(0, 3000, 0), Radius: 40, Height: 60 } });
  const parts = [['Body', 'Sphere', [0, 0, 0], [0.9, 0.9, 0.9], 'DroneBody'], ['Visor', 'Cube', [0, 12, 38], [0.55, 0.14, 0.1], 'DroneEye'], ['Gun', 'Cube', [28, -12, 40], [0.08, 0.08, 0.45], 'DroneBody'], ['Skid', 'Cube', [0, -42, 0], [0.7, 0.06, 0.7], 'Accent']];
  for (const [name, shape, p, s, mat] of parts) {
    const part = await call('spawn', { type: 'FlaxEngine.StaticModel', name, parent: drone.id, properties: { LocalPosition: V(...p), Scale: V(...s), Model: `$engine/Editor/Primitives/${shape}.flax` } });
    await call('material', { id: part.id, path: `Materials/${mat}.flax` });
  }
  await call('spawn', { type: 'FlaxEngine.PointLight', name: 'Eye', parent: drone.id, properties: { LocalPosition: V(0, 12, 60), Color: { R: 1, G: 0.1, B: 0.05, A: 1 }, Radius: 260, Brightness: 4 } });
  await call('add_script', { id: drone.id, type: 'Game.Health', properties: { Max: 100 } });
  await call('add_script', { id: drone.id, type: 'Game.Enemy' });
  await call('prefab_create', { id: drone.id, path: 'Prefabs/Drone.prefab' });
  await call('delete', { id: drone.id });
}

try {
  const status = await call('status');
  if (status.play) throw Error('Stop play mode first');
  let tree = (await call('scene_tree', { depth: 1 }))[0];
  const present = tree.children.filter(a => owned.includes(a.name));
  if (present.some(a => a.name !== 'MainCamera') && !process.argv.includes('--rebuild')) throw Error('Arena already built; pass --rebuild to replace it');
  for (const a of present) await call('delete', { id: a.id });
  await materials();
  await call('material', { id: tree.children.find(a => a.name === 'Ground').id, path: 'Materials/Concrete.flax' });
  await sounds();
  await arena();
  const body = await player();
  await dronePrefab();
  const spawns = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'SpawnPoints' });
  for (const [i, [x, z]] of [[-1800, 1800], [0, 1850], [1800, 1800], [1850, 0], [1800, -1800], [-1800, -1800], [-1850, 0], [-900, 1600]].entries())
    await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'Spawn_' + i, parent: spawns.id, properties: { Position: V(x, 120, z) } });
  const director = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'GameDirector' });
  await call('add_script', { id: director.id, type: 'Game.ShooterGame', properties: { Player: body.id, SpawnPoints: spawns.id } });
  await call('spawn', { type: 'FlaxEngine.NavMeshBoundsVolume', name: 'NavBounds', properties: { Position: V(0, 150, 0), Size: V(4200, 600, 4200) } });
  await call('save');
  const scene = { $ref: { kind: 'actor', id: tree.id } };
  await call('call', { target: { kind: 'static', type: 'FlaxEngine.Navigation' }, method: 'BuildNavMesh', parameterTypes: ['FlaxEngine.Scene', 'System.Single'], args: [scene, 0] });
  await wait(500);
  for (let i = 0; i < 120 && (await call('get', { target: { kind: 'static', type: 'FlaxEngine.Navigation' }, members: ['IsBuildingNavMesh'] })).IsBuildingNavMesh.value; i++) await wait(250);
  const path = await call('call', { target: { kind: 'static', type: 'FlaxEngine.Navigation' }, method: 'FindPath', args: [V(-1800, 50, 1800), V(0, 50, -1500)] });
  console.log(`Navmesh path corner to player: ${path.value ? Object.values(path.out)[0].length + ' points' : 'NOT FOUND'}`);
  await call('save');
  console.log('Arena built and saved.');
} finally { await client.close(); }

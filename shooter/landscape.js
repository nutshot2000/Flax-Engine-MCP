// Surrounds the arena with terrain and foliage using flax_terrain and flax_foliage.
// Usage: FLAX_PROJECT=D:/FlaxProjects/BridgeGame node shooter/landscape.js [--rebuild]
import { connect } from '../tests/client.js';
const { client, call } = await connect();
const color = (R, G, B) => ({ $type: 'FlaxEngine.Color', value: { R, G, B, A: 1 } });
const owned = ['Landscape', 'LandscapeFoliage', 'HeightFog'];
// The arena floor's top is at y=0 and its walls reach x/z = +/-2100 (corners ~2970 from the centre).
const floorY = -5, arenaClear = 3300;

try {
  if ((await call('status')).play) throw Error('Stop play mode first');
  const tree = (await call('scene_tree', { depth: 1 }))[0];
  const present = tree.children.filter(a => owned.includes(a.name));
  if (present.length && !process.argv.includes('--rebuild')) throw Error('Landscape already built; pass --rebuild to replace it');
  for (const a of present) await call('delete', { id: a.id });

  const existing = JSON.parse((await client.callTool({ name: 'flax_assets', arguments: { filter: 'Materials' } })).content[0].text).files.join('\n');
  for (const [name, rgb] of Object.entries({ Pine: [0.06, 0.2, 0.09], Bush: [0.16, 0.32, 0.1], Rock: [0.36, 0.35, 0.33] }))
    if (!existing.includes(name + '.flax')) await call('material_create', { path: `Materials/${name}.flax`, parameters: { Color: color(...rgb) } });

  // 2x2 patches of 252 m centred on the arena.
  const terrain = await call('terrain', { action: 'create', name: 'Landscape', position: { X: -25200, Y: floorY, Z: -25200 }, chunkSize: 63,
    patches: [{ x: 0, z: 0 }, { x: 1, z: 0 }, { x: 0, z: 1 }, { x: 1, z: 1 }], material: '$engine/Engine/DefaultTerrainMaterial.flax' });
  const sculpt = args => call('terrain', { id: terrain.id, action: 'sculpt', center: { X: 0, Z: 0 }, ...args });
  await sculpt({ mode: 'noise', radius: 26000, innerRadius: arenaClear, falloff: 0.9, strength: 1800, noiseScale: 6000, octaves: 5, seed: 11 });
  await sculpt({ mode: 'raise', radius: 26000, innerRadius: 9000, falloff: 0.85, strength: 3500 });
  await sculpt({ mode: 'noise', radius: 26000, innerRadius: 12000, falloff: 0.7, strength: 2500, noiseScale: 2500, octaves: 4, seed: 23 });
  await sculpt({ mode: 'smooth', radius: 26000, strength: 0.6 });
  // Keep the ground around the walls level with the arena floor, easing into the hills.
  await sculpt({ mode: 'set', radius: 5200, falloff: 0.35, height: floorY });
  await call('terrain', { id: terrain.id, action: 'paint', layer: 1, center: { X: 0, Z: 0 }, radius: 26000, innerRadius: 7000, falloff: 0.6, strength: 1 });

  const foliage = await call('foliage', { action: 'create', name: 'LandscapeFoliage' });
  const type = async (model, material) => (await call('foliage', { action: 'add_type', id: foliage.id, model: `$engine/Editor/Primitives/${model}.flax`, material: `Materials/${material}.flax`, cullDistance: 30000 })).index;
  const pine = await type('Cone', 'Pine'), bush = await type('Sphere', 'Bush'), rock = await type('Cube', 'Rock');
  const scatter = args => call('foliage', { action: 'scatter', id: foliage.id, center: { X: 0, Z: 0 }, onlyOn: terrain.id, baseOffset: 50, ...args });
  const results = {
    pines: await scatter({ type: pine, innerRadius: arenaClear + 400, radius: 24000, count: 700, scaleMin: 2.5, scaleMax: 5, heightScale: 2.4, maxSlope: 32, minSpacing: 350, seed: 1 }),
    bushes: await scatter({ type: bush, innerRadius: arenaClear, radius: 16000, count: 450, scaleMin: 0.7, scaleMax: 1.8, heightScale: 0.7, maxSlope: 40, seed: 2 }),
    rocks: await scatter({ type: rock, innerRadius: arenaClear, radius: 22000, count: 180, scaleMin: 0.6, scaleMax: 2.6, heightScale: 0.6, alignToNormal: true, baseOffset: 25, seed: 3 }),
  };
  await call('spawn', { type: 'FlaxEngine.ExponentialHeightFog', name: 'HeightFog', properties: { FogDensity: 0.02, FogHeightFalloff: 0.002, StartDistance: 2500 } });
  await call('save');
  console.log(JSON.stringify(Object.fromEntries(Object.entries(results).map(([k, r]) => [k, r.placed]))), 'foliage placed; landscape saved.');
} finally { await client.close(); }

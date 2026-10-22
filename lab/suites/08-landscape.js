import assert from 'node:assert/strict';
export const area = 'landscape';
const dir = process.env.LAB_DIR;
// Far from other suites' actors. chunkSize 31 => 124 m patches; two patches along X.
const origin = { X: 20000, Y: -5000, Z: 20000 };
const at = (dx, dz) => ({ X: origin.X + dx, Z: origin.Z + dz });
const state = {};
const terrain = (call, args) => call('terrain', { id: state.terrain, ...args });
const heightAt = async (call, p) => (await terrain(call, { action: 'sample', points: [p] }))[0].Y - origin.Y;
const near = (got, want, tolerance, label) => assert(Math.abs(got - want) <= tolerance, `${label}: ${got} vs ${want}`);

export const tests = [
  { name: 'terrain create with patches', run: async ({ call }) => {
    const t = await call('terrain', { action: 'create', name: 'LabTerrain', position: origin, chunkSize: 31, lods: 4, patches: [{ x: 0, z: 0 }, { x: 1, z: 0 }] });
    state.terrain = t.id;
    assert.equal(t.patches.length, 2); assert.equal(t.heightmapSize, 125); assert.equal(t.patchWorldSize, 12400);
    near(await heightAt(call, at(1000, 1000)), 0, 1, 'flat after create');
    return `${t.patches.length} patches, ${t.patchWorldSize / 100} m each`;
  } },
  { name: 'terrain raise is placed at the right X/Z', run: async ({ call }) => {
    await terrain(call, { action: 'sculpt', mode: 'raise', center: at(3000, 8000), radius: 1500, falloff: 0, strength: 400 });
    near(await heightAt(call, at(3000, 8000)), 400, 2, 'raised point');
    near(await heightAt(call, at(8000, 3000)), 0, 2, 'mirrored point untouched');
  } },
  { name: 'terrain set and flatten toward a world height', run: async ({ call }) => {
    await terrain(call, { action: 'sculpt', mode: 'set', center: at(15000, 6000), radius: 1200, falloff: 0, height: origin.Y + 900 });
    near(await heightAt(call, at(15000, 6000)), 900, 2, 'set (second patch)');
    await terrain(call, { action: 'sculpt', mode: 'flatten', center: at(15000, 6000), radius: 1200, falloff: 0, height: origin.Y + 100, strength: 0.5 });
    near(await heightAt(call, at(15000, 6000)), 500, 2, 'half-way flatten');
  } },
  { name: 'terrain smooth lowers a sharp peak', run: async ({ call }) => {
    await terrain(call, { action: 'sculpt', mode: 'raise', center: at(6000, 3000), radius: 150, falloff: 0, strength: 1000 });
    const before = await heightAt(call, at(6000, 3000));
    await terrain(call, { action: 'sculpt', mode: 'smooth', center: at(6000, 3000), radius: 800, falloff: 0, strength: 1 });
    const after = await heightAt(call, at(6000, 3000));
    assert(after < before - 100, `peak ${before} -> ${after}`);
  } },
  { name: 'terrain noise varies heights', run: async ({ call }) => {
    const points = [at(9000, 9000), at(9500, 10200), at(10400, 9300)];
    await terrain(call, { action: 'sculpt', mode: 'noise', center: at(10000, 10000), radius: 2500, falloff: 0, strength: 300, noiseScale: 800, seed: 7 });
    const first = await terrain(call, { action: 'sample', points });
    const spread = Math.max(...first.map(p => p.Y)) - Math.min(...first.map(p => p.Y));
    assert(spread > 10, 'noise varies heights: ' + spread);
    return `spread ${spread.toFixed(0)} cm`;
  } },
  { name: 'terrain paint blends a splat layer', run: async ({ call, exec }) => {
    const r = await terrain(call, { action: 'paint', layer: 2, center: at(2000, 2000), radius: 600, falloff: 0, strength: 1 });
    assert(r.vertices > 0);
    // Vertex (20, 20) of patch (0,0) sits at local (2000, 2000); layer 2 = splatmap 0, blue channel.
    const weights = await exec(`var t = (Terrain)ctx.Find("${state.terrain}"); var p = new Int2(0, 0); unsafe { var d = t.GetPatch(ref p).GetSplatMapData(0); var c = d[20 * 125 + 20]; return $"{c.R},{c.G},{c.B},{c.A}"; }`);
    assert.equal(weights, '0,0,255,0');
  } },
  { name: 'terrain edits survive save and reload', run: async lab => {
    const { call } = lab;
    await call('save');
    const scene = (await call('status')).scenes.find(s => s.id === lab.sceneId);
    await call('unload_scene', { id: lab.sceneId });
    await call('open_scene', { path: `${dir}/Run.scene` });
    near(await heightAt(call, at(3000, 8000)), 400, 2, 'raised point after reload');
    return scene?.path;
  } },
  { name: 'foliage types, scatter on terrain only, clear', run: async ({ call }) => {
    const f = await call('foliage', { action: 'create', name: 'LabFoliage' });
    state.foliage = f.id;
    const cones = await call('foliage', { action: 'add_type', id: f.id, model: '$engine/Editor/Primitives/Cone.flax', cullDistance: 30000 });
    const rocks = await call('foliage', { action: 'add_type', id: f.id, model: '$engine/Editor/Primitives/Cube.flax' });
    await assert.rejects(call('foliage', { action: 'add_type', id: f.id, model: '$engine/Editor/Primitives/Cube.flax' }), /distinct model/);
    const placed = await call('foliage', { action: 'scatter', id: f.id, type: cones.index, center: at(6200, 6200), radius: 5000, count: 60, scaleMin: 0.5, scaleMax: 2, heightScale: 3, baseOffset: 50, onlyOn: state.terrain, seed: 3 });
    assert.equal(placed.placed, 60);
    // Stretched vertically, and lifted so the cone's base (50 below its pivot) rests on the terrain.
    const first = await call('exec', { code: `var f = (Foliage)ctx.Find("${f.id}"); var i = f.GetInstance(0); var t = (Terrain)ctx.Find("${state.terrain}"); var o = new Vector3(i.Transform.Translation.X, 100000, i.Transform.Translation.Z); t.RayCast(o, Vector3.Down, out float d, out TerrainChunk c, float.MaxValue); return new[] { i.Transform.Scale.Y / i.Transform.Scale.X, (float)(i.Transform.Translation.Y - (o.Y - d)) / i.Transform.Scale.Y };` });
    near(first.value[0], 3, 1e-3, 'height scale'); near(first.value[1], 50, 0.5, 'base offset');
    await call('foliage', { action: 'scatter', id: f.id, type: rocks.index, center: at(6200, 6200), radius: 5000, count: 20, minSpacing: 400, alignToNormal: true, onlyOn: state.terrain });
    const info = await call('foliage', { action: 'info', id: f.id });
    assert.equal(info.instances, 80);
    // A far-away blocker is not the terrain, so scatter there is rejected.
    const blocker = await call('spawn', { type: 'FlaxEngine.BoxCollider', name: 'NotTerrain', properties: { Position: { X: -20000, Y: 0, Z: -20000 }, Size: { X: 2000, Y: 100, Z: 2000 } } });
    const off = await call('foliage', { action: 'scatter', id: f.id, type: 0, center: { X: -20000, Z: -20000 }, radius: 800, count: 5, onlyOn: state.terrain });
    assert.equal(off.placed, 0); assert(off.rejectedSurface > 0);
    await call('delete', { id: blocker.id });
    const cleared = await call('foliage', { action: 'clear', id: f.id, type: rocks.index });
    assert.equal(cleared.removed, 20); assert.equal(cleared.info.instances, 60);
  } },
  { name: 'vertex paint fill, gradient and clear', run: async ({ call }) => {
    const cube = await call('spawn', { type: 'FlaxEngine.StaticModel', name: 'PaintCube', properties: { Model: '$engine/Editor/Primitives/Cube.flax' } });
    const target = { kind: 'actor', id: cube.id };
    const color = async v => (await call('call', { target, method: 'GetVertexColor', args: [0, 0, v] })).value;
    const fill = await call('vertex_paint', { id: cube.id, action: 'fill', color: { R: 1, G: 0, B: 0 } });
    assert(fill.vertices > 0);
    assert.deepEqual([(await color(0)).R, (await color(0)).G], [255, 0]);
    const gradient = await call('vertex_paint', { id: cube.id, action: 'gradient', from: { R: 0, G: 0, B: 1 }, to: { R: 1, G: 1, B: 0 }, axis: 'Y' });
    const samples = await Promise.all(Array.from({ length: Math.min(24, gradient.vertices) }, (_, i) => color(i)));
    assert(new Set(samples.map(c => c.B)).size > 1, 'gradient gives different colours');
    await call('vertex_paint', { id: cube.id, action: 'clear' });
    assert.equal((await call('get', { target, members: ['HasVertexColors'] })).HasVertexColors.value, false);
  } },
];

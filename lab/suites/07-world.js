import assert from 'node:assert/strict';
export const area = 'world';
const V = (X, Y, Z) => ({ X, Y, Z });
export const tests = [
  { name: 'terrain: setup, add patch (ref arg), sculpt via exec', run: async ({ call, exec }) => {
    const t = await call('spawn', { type: 'FlaxEngine.Terrain', name: 'LabTerrain', properties: { Position: V(-5000, -2000, -5000) } });
    await call('call', { target: { kind: 'actor', id: t.id }, method: 'Setup', args: [4, 63] });
    await call('call', { target: { kind: 'actor', id: t.id }, method: 'AddPatch', parameterTypes: ['FlaxEngine.Int2&'], args: [{ X: 0, Y: 0 }] });
    const patches = (await call('get', { target: { kind: 'actor', id: t.id }, members: ['PatchesCount'] })).PatchesCount.value;
    assert.equal(patches, 1);
    // AddPatch only reserves the patch; the editor workflow then creates its heightmap.
    const init = await call('call', { target: { kind: 'static', type: 'FlaxEditor.TerrainTools' }, method: 'InitializePatch', args: [{ $ref: { kind: 'actor', id: t.id } }, { X: 0, Y: 0 }] });
    assert.equal(init.value, false, 'InitializePatch reports failure as true');
    // Heightmap APIs take and return raw float pointers, so this part goes through exec. A bad pointer would crash the editor, so check it.
    const sample = await exec(`
      var terrain = (Terrain)ctx.Find("${t.id}");
      var patch = new Int2(0, 0);
      if (TerrainTools.GetHeightmapData(terrain, ref patch) == null) throw new InvalidOperationException("Heightmap not initialized");
      int size = terrain.ChunkSize * 4 + 1;
      var heights = new float[size * size];
      for (int i = 0; i < heights.Length; i++) heights[i] = 300f;
      var offset = new Int2(0, 0); var extent = new Int2(size, size);
      fixed (float* p = heights) TerrainTools.ModifyHeightMap(terrain, ref patch, p, ref offset, ref extent);
      return TerrainTools.GetHeightmapData(terrain, ref patch)[size * size / 2];`);
    assert(Math.abs(sample - 300) < 1, 'sculpted height ' + sample);
  } },
  { name: 'navigation: build navmesh and find path (out array)', run: async lab => {
    const { call, until, wait } = lab;
    await call('spawn', { type: 'FlaxEngine.BoxCollider', name: 'NavFloor', properties: { Position: V(0, 3000, 0), Size: V(3000, 20, 3000) } });
    await call('spawn', { type: 'FlaxEngine.NavMeshBoundsVolume', name: 'NavBounds', properties: { Position: V(0, 3000, 0), Size: V(3200, 600, 3200) } });
    await call('call', { target: { kind: 'static', type: 'FlaxEngine.Navigation' }, method: 'BuildNavMesh', parameterTypes: ['FlaxEngine.Scene', 'System.Single'], args: [{ $ref: { kind: 'actor', id: lab.sceneId } }, 0] });
    await wait(300);
    await until(async () => !(await call('get', { target: { kind: 'static', type: 'FlaxEngine.Navigation' }, members: ['IsBuildingNavMesh'] })).IsBuildingNavMesh.value, { timeout: 30000, what: 'navmesh build' });
    const r = await call('call', { target: { kind: 'static', type: 'FlaxEngine.Navigation' }, method: 'FindPath', args: [V(-1200, 3010, -1200), V(1200, 3010, 1200)] });
    assert.equal(r.value, true, 'path found');
    const points = Object.values(r.out)[0];
    assert(points.length >= 2);
    return `${points.length} path points`;
  } },
  { name: 'spline: add points and measure', run: async ({ call }) => {
    const s = await call('spawn', { type: 'FlaxEngine.Spline', name: 'LabSpline' });
    const spline = { kind: 'actor', id: s.id };
    // New splines start with default points; replace them with a straight 1000-unit line.
    await call('call', { target: spline, method: 'ClearSpline' });
    for (const p of [V(0, 0, 0), V(1000, 0, 0)]) await call('call', { target: spline, method: 'AddSplineLocalPoint', parameterTypes: ['FlaxEngine.Vector3', 'System.Boolean'], args: [p, true] });
    const r = await call('get', { target: spline, members: ['SplinePointsCount', 'SplineLength'] });
    assert.equal(r.SplinePointsCount.value, 2);
    assert(Math.abs(r.SplineLength.value - 1000) < 1, 'length ' + r.SplineLength.value);
  } },
  { name: 'UI: canvas with a label control', run: async ({ call }) => {
    const canvas = await call('spawn', { type: 'FlaxEngine.UICanvas', name: 'LabCanvas' });
    const control = await call('spawn', { type: 'FlaxEngine.UIControl', name: 'LabLabel', parent: canvas.id, properties: { Control: { $new: 'FlaxEngine.GUI.Label' } } });
    await call('object_set', { target: { kind: 'actor', id: control.id, member: 'Control' }, properties: { Text: 'Hello bridge' } });
    const r = await call('get', { target: { kind: 'actor', id: control.id, member: 'Control' }, members: ['Text'] });
    assert.match(JSON.stringify(r.Text.value), /Hello bridge/);
  } },
  { name: 'CSG brush', run: async ({ call }) => {
    const b = await call('spawn', { type: 'FlaxEngine.BoxBrush', name: 'LabBrush', properties: { Size: V(200, 300, 400) } });
    const r = await call('get', { target: { kind: 'actor', id: b.id }, members: ['Size', 'Mode'] });
    assert.equal(r.Size.value.Z, 400);
  } },
];
// Spawn every common actor type and round-trip one representative property.
const actors = [
  ['PointLight', { Brightness: 3 }], ['SpotLight', { OuterConeAngle: 30 }], ['DirectionalLight', { Brightness: 2 }], ['SkyLight', { Brightness: 1.5 }],
  ['Sky', {}], ['EnvironmentProbe', { Radius: 900 }], ['ExponentialHeightFog', { FogDensity: 0.05 }], ['Camera', { FieldOfView: 75 }],
  ['PostFxVolume', { Size: V(500, 500, 500) }], ['Decal', { Size: V(100, 100, 100) }], ['AudioSource', { Volume: 0.5 }], ['AudioListener', {}],
  ['ParticleEffect', { SimulationSpeed: 2 }], ['AnimatedModel', { UpdateWhenOffscreen: true }], ['TextRender', { Text: 'Lab' }], ['SpriteRender', { Size: { X: 64, Y: 32 } }],
  ['Foliage', {}], ['CharacterController', { Radius: 40 }], ['SphereCollider', { Radius: 75 }],
  ['CapsuleCollider', { Height: 120 }], ['MeshCollider', {}], ['HingeJoint', {}], ['NavLink', { Radius: 50 }], ['NavModifierVolume', {}],
];
for (const [type, props] of actors) tests.push({ name: `actor ${type}`, run: async ({ call }) => {
  const a = await call('spawn', { type: 'FlaxEngine.' + type, name: 'Lab' + type, properties: props });
  const keys = Object.keys(props);
  if (!keys.length) return 'spawned';
  const r = await call('get', { target: { kind: 'actor', id: a.id }, members: keys });
  for (const k of keys) {
    if (r[k].error) throw Error(`${k}: ${r[k].error}`);
    const got = r[k].value, want = props[k];
    if (typeof want === 'object') for (const c of Object.keys(want)) assert(Math.abs(got[c] - want[c]) < 1e-3, `${k}.${c}`);
    else if (typeof want === 'number') assert(Math.abs(got - want) < 1e-3, `${k}=${JSON.stringify(got)}`);
    else assert.match(JSON.stringify(got), new RegExp(String(want)));
  }
} });

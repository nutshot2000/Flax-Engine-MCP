import assert from 'node:assert/strict';
export const area = 'core';
const dir = process.env.LAB_DIR;
const empty = (call, name, properties) => call('spawn', { type: 'FlaxEngine.EmptyActor', name, properties });
export const tests = [
  { name: 'status reports capabilities and content folders', run: async ({ call }) => {
    const s = await call('status');
    for (const c of ['ordered-queue', 'exec', 'ref-out-call', 'undo-set']) assert(s.capabilities.includes(c), 'missing capability ' + c);
    assert(s.engineContent && s.projectContent);
    return s.version;
  } },
  { name: 'spawn, inspect, set and delete round trip', run: async ({ call }) => {
    const a = await empty(call, 'RoundTrip', { Position: { X: 10, Y: 20, Z: 30 } });
    assert.deepEqual(a.position, [10, 20, 30]);
    const props = await call('inspect', { id: a.id });
    assert(props.some(p => p.name === 'Position' && p.writable));
    const updated = await call('set', { id: a.id, properties: { Position: { X: 1, Y: 2, Z: 3 }, Name: 'Renamed' } });
    assert.equal(updated.name, 'Renamed'); assert.deepEqual(updated.position, [1, 2, 3]);
    await call('delete', { id: a.id });
    assert.equal((await call('find', { name: 'Renamed' })).length, 0);
  } },
  { name: 'set is one undoable step', run: async ({ call }) => {
    const a = await empty(call, 'UndoTarget');
    await call('set', { id: a.id, properties: { Position: { X: 500, Y: 0, Z: 0 } } });
    await call('undo');
    assert.deepEqual((await call('scene_tree', { id: a.id, depth: 0 }))[0].position, [0, 0, 0]);
    await call('redo');
    assert.deepEqual((await call('scene_tree', { id: a.id, depth: 0 }))[0].position, [500, 0, 0]);
  } },
  { name: 'children inherit a scaled parent (collider fits its model)', run: async ({ call }) => {
    const model = await call('spawn', { type: 'FlaxEngine.StaticModel', name: 'ScaledWall', properties: { Position: { X: 0, Y: 200, Z: 3000 }, Scale: { X: 40, Y: 4, Z: 1 }, Model: '$engine/Editor/Primitives/Cube.flax' } });
    const collider = await call('spawn', { type: 'FlaxEngine.BoxCollider', name: 'ScaledWallCollider', parent: model.id });
    const r = await call('get', { target: { kind: 'actor', id: collider.id }, members: ['Scale', 'Position'] });
    assert.deepEqual([r.Scale.value.X, r.Scale.value.Y, r.Scale.value.Z], [40, 4, 1]);
    assert.equal(r.Position.value.Z, 3000);
    const hit = await call('call', { target: { kind: 'static', type: 'FlaxEngine.Physics' }, method: 'RayCast',
      parameterTypes: ['FlaxEngine.Vector3', 'FlaxEngine.Vector3', 'FlaxEngine.RayCastHit&', 'System.Single', 'System.UInt32', 'System.Boolean'],
      args: [{ X: 1500, Y: 200, Z: 2000 }, { X: 0, Y: 0, Z: 1 }] });
    assert.equal(hit.value, true, 'ray near the wall edge hits its collider');
  } },
  { name: 'child Position is world-space, LocalPosition parent-relative', run: async ({ call }) => {
    const parent = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'Offset', properties: { Position: { X: 1000, Y: 0, Z: 0 } } });
    const world = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'WorldChild', parent: parent.id, properties: { Position: { X: 50, Y: 0, Z: 0 } } });
    const local = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'LocalChild', parent: parent.id, properties: { LocalPosition: { X: 50, Y: 0, Z: 0 } } });
    assert.equal(world.position[0], 50); assert.equal(local.position[0], 1050);
  } },
  { name: 'scene_tree depth and childCount', run: async ({ call }) => {
    const parent = await empty(call, 'TreeParent');
    await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'TreeChild', parent: parent.id });
    const [node] = await call('scene_tree', { id: parent.id, depth: 0 });
    assert.equal(node.childCount, 1); assert.equal(node.children, null);
    const [deep] = await call('scene_tree', { id: parent.id });
    assert.equal(deep.children[0].name, 'TreeChild');
  } },
  { name: 'find by name and by type', run: async ({ call }) => {
    await call('spawn', { type: 'FlaxEngine.PointLight', name: 'FindMeLight' });
    assert.equal((await call('find', { name: 'findmelight' })).length, 1);
    assert((await call('find', { type: 'FlaxEngine.Light' })).some(a => a.name === 'FindMeLight'), 'subclass match');
  } },
  { name: 'parallel tool calls apply in submission order', run: async ({ call }) => {
    const a = await empty(call, 'OrderProbe');
    await Promise.all(Array.from({ length: 24 }, (_, i) => call('set', { id: a.id, properties: { Position: { X: i, Y: 0, Z: 0 } } })));
    const [node] = await call('scene_tree', { id: a.id, depth: 0 });
    assert.equal(node.position[0], 23);
  } },
  { name: 'batch reports partial failure and stops', run: async ({ call }) => {
    const r = await call('batch', { operations: [
      { op: 'spawn', args: { type: 'FlaxEngine.EmptyActor', name: 'BatchA' } },
      { op: 'spawn', args: { type: 'FlaxEngine.NoSuchActor', name: 'BatchBad' } },
      { op: 'spawn', args: { type: 'FlaxEngine.EmptyActor', name: 'BatchC' } }] });
    assert.equal(r.results.length, 2); assert(r.results[0].ok); assert(!r.results[1].ok);
    assert.match(r.results[1].error, /Type not found/);
  } },
  { name: 'STOP latch blocks mutations until resumed', run: async ({ call }) => {
    await call('stop');
    try { await assert.rejects(empty(call, 'Blocked'), /Bridge stopped/); }
    finally { await call('resume_control'); }
    await empty(call, 'AfterResume');
  } },
  { name: 'create_scene reports defaults and can start empty', run: async ({ call, sceneId }) => {
    const full = await call('create_scene', { path: `${dir}/Defaults_${Date.now()}.scene` });
    assert(full.children.some(c => c.type === 'FlaxEngine.DirectionalLight'), 'default scene lists its sun');
    const empty = await call('create_scene', { path: `${dir}/Empty_${Date.now()}.scene`, empty: true });
    assert.equal(empty.children.length, 0);
    for (const id of [full.id, empty.id]) await call('unload_scene', { id, discard: true });
  } },
  { name: 'unknown actor gives a clear error', run: async ({ call }) => {
    await assert.rejects(call('set', { id: '00000000-0000-0000-0000-000000000001', properties: {} }), /Actor not found/);
  } },
];

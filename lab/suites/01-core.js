import assert from 'node:assert/strict';
export const area = 'core';
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
  { name: 'unknown actor gives a clear error', run: async ({ call }) => {
    await assert.rejects(call('set', { id: '00000000-0000-0000-0000-000000000001', properties: {} }), /Actor not found/);
  } },
];

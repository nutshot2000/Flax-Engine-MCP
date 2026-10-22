import assert from 'node:assert/strict';
export const area = 'reflection';
const V = (X, Y, Z) => ({ X, Y, Z });
export const tests = [
  { name: 'types finds engine and editor types', run: async ({ call }) => {
    const t = await call('types', { filter: 'TerrainTools' });
    assert(t.some(x => x.name === 'FlaxEditor.TerrainTools'));
    return `${(await call('types', { filter: 'FlaxEngine.', limit: 2000 })).length} FlaxEngine types visible`;
  } },
  { name: 'api reports ref/out parameters and generic methods', run: async ({ call }) => {
    const api = await call('api', { type: 'FlaxEngine.Physics', member: 'RayCast' });
    assert(api.methods.some(m => m.parameters.some(p => p.out)), 'RayCast out hit');
    const level = await call('api', { type: 'FlaxEngine.Level', member: 'FindActor' });
    assert(level.methods.some(m => m.genericArguments));
  } },
  { name: 'get static members', run: async ({ call }) => {
    const r = await call('get', { target: { kind: 'static', type: 'FlaxEngine.Globals' }, members: ['EngineContentFolder', 'ProjectFolder'] });
    assert(r.EngineContentFolder.value && r.ProjectFolder.value);
  } },
  { name: 'call with $new arguments and exact overload', run: async ({ call }) => {
    const r = await call('call', { target: { kind: 'static', type: 'FlaxEngine.Vector3' }, method: 'Distance', parameterTypes: ['FlaxEngine.Vector3', 'FlaxEngine.Vector3'],
      args: [{ $new: 'FlaxEngine.Vector3', args: [0, 0, 0] }, V(3, 4, 0)] });
    assert(Math.abs(r.value - 5) < 1e-4);
  } },
  { name: 'call generic method with typeArguments', run: async ({ call }) => {
    await call('spawn', { type: 'FlaxEngine.SpotLight', name: 'GenericProbe' });
    const r = await call('call', { target: { kind: 'static', type: 'FlaxEngine.Level' }, method: 'FindActor', typeArguments: ['FlaxEngine.SpotLight'], parameterTypes: [] });
    assert.equal(r.value.type, 'FlaxEngine.SpotLight');
  } },
  { name: 'call returns out parameters (Physics.RayCast hit)', run: async ({ call, wait }) => {
    await call('spawn', { type: 'FlaxEngine.BoxCollider', name: 'RayTarget', properties: { Position: V(0, -1000, 0), Size: V(400, 100, 400) } });
    await wait(200);
    const r = await call('call', { target: { kind: 'static', type: 'FlaxEngine.Physics' }, method: 'RayCast',
      parameterTypes: ['FlaxEngine.Vector3', 'FlaxEngine.Vector3', 'FlaxEngine.RayCastHit&', 'System.Single', 'System.UInt32', 'System.Boolean'],
      args: [V(0, -500, 0), V(0, -1, 0)] });
    assert.equal(r.value, true, 'ray should hit the collider');
    const hit = Object.values(r.out)[0];
    assert(Math.abs(hit.Point.Y - -950) < 1, 'hit point at collider top: ' + JSON.stringify(hit.Point));
  } },
  { name: 'object_set via member path is undoable', run: async ({ call }) => {
    const light = await call('spawn', { type: 'FlaxEngine.PointLight', name: 'ObjectSetLight' });
    await call('object_set', { target: { kind: 'actor', id: light.id }, properties: { Brightness: 7.5 } });
    assert.equal((await call('get', { target: { kind: 'actor', id: light.id }, members: ['Brightness'] })).Brightness.value, 7.5);
    await call('undo');
    assert.notEqual((await call('get', { target: { kind: 'actor', id: light.id }, members: ['Brightness'] })).Brightness.value, 7.5);
  } },
  { name: 'object target resolves any object by ID', run: async ({ call }) => {
    const a = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'ById' });
    const r = await call('get', { target: { kind: 'object', id: a.id }, members: ['Name'] });
    assert.equal(r.Name.value, 'ById');
  } },
];

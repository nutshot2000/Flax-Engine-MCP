import assert from 'node:assert/strict';
export const area = 'playmode';
const V = (X, Y, Z) => ({ X, Y, Z });
async function play({ call, until }) {
  await call('play');
  await until(async () => (await call('status')).play, { what: 'play mode' });
}
async function stop({ call, until }) {
  await call('stop_play');
  await until(async () => !(await call('status')).play, { what: 'edit mode' });
}
const ticks = async (call, id) => (await call('get', { target: { kind: 'script', id, type: 'Game.LabProbe' }, members: ['Ticks'] })).Ticks.value;
export const tests = [
  { name: 'scene view screenshot', run: async ({ tool }) => {
    const r = await tool('screenshot', { view: 'scene' });
    assert.equal(r.content[0].type, 'image');
    return `${Math.round(r.content[0].data.length * 0.75 / 1024)} KB png`;
  } },
  { name: 'play, runtime reads, pause and stop', run: async lab => {
    const { call, wait } = lab;
    const host = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'PlayProbe' });
    await call('add_script', { id: host.id, type: 'Game.LabProbe' });
    await play(lab);
    try {
      // Play mode runs a copy of the scene; locate the runtime actor by name.
      const [runtime] = await lab.until(async () => { const f = await call('find', { name: 'PlayProbe' }); return f.length ? f : null; }, { what: 'runtime actor' });
      const first = await ticks(call, runtime.id); await wait(400);
      const second = await ticks(call, runtime.id);
      assert(second > first, `script updates during play (${first} -> ${second})`);
      await call('pause'); await wait(300);
      const frozen = await ticks(call, runtime.id); await wait(400);
      assert.equal(await ticks(call, runtime.id), frozen, 'paused');
      await call('resume_play');
      const shot = await lab.tool('screenshot', { view: 'game' });
      assert.equal(shot.content[0].type, 'image');
      await assert.rejects(call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'DuringPlay' }), /Stop play/);
    } finally { await stop(lab); }
  } },
  { name: 'runtime edits require runtime=true', run: async lab => {
    const { call } = lab;
    const a = await call('spawn', { type: 'FlaxEngine.PointLight', name: 'RuntimeLight' });
    await play(lab);
    try {
      const [runtime] = await call('find', { name: 'RuntimeLight' });
      await assert.rejects(call('object_set', { target: { kind: 'actor', id: runtime.id }, properties: { Brightness: 2 } }), /runtime=true/);
      await call('object_set', { target: { kind: 'actor', id: runtime.id }, properties: { Brightness: 2 }, runtime: true });
    } finally { await stop(lab); }
    assert.notEqual((await call('get', { target: { kind: 'actor', id: a.id }, members: ['Brightness'] })).Brightness.value, 2, 'edit-time value restored');
  } },
  { name: 'rigid body falls under simulation', run: async lab => {
    const { call, wait } = lab;
    const body = await call('spawn', { type: 'FlaxEngine.RigidBody', name: 'Faller', properties: { Position: V(2000, 500, 0) } });
    await call('spawn', { type: 'FlaxEngine.BoxCollider', name: 'FallerShape', parent: body.id });
    await play(lab);
    try {
      await wait(1000);
      const [runtime] = await call('find', { name: 'Faller', type: 'FlaxEngine.RigidBody' });
      assert(runtime.position[1] < 400, 'fell to ' + runtime.position[1]);
      return `y=${runtime.position[1].toFixed(0)} after 1 s`;
    } finally { await stop(lab); }
  } },
];

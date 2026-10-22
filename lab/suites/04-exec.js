import assert from 'node:assert/strict';
export const area = 'exec';
export const tests = [
  { name: 'expression returns a value', run: async ({ exec }) => { assert.equal(await exec('1 + 2'), 3); } },
  { name: 'statements, logs and engine access', run: async ({ call }) => {
    const r = await call('exec', { code: 'foreach (var s in Level.Scenes) ctx.Log(s.Name); return Level.ScenesCount;' });
    assert(r.value >= 1); assert.equal(r.logs.length, r.value);
  } },
  { name: 'editor internals are reachable', run: async ({ exec }) => {
    assert.equal(typeof await exec('ctx.Editor.Windows.EditWin.Viewport.ViewPosition.ToString()'), 'string');
  } },
  { name: 'compile errors report snippet line numbers', run: async ({ call }) => {
    await assert.rejects(call('exec', { code: 'var x = 1;\nreturn y;' }), /\(2,\d+\).*CS0103/);
  } },
  { name: 'runtime exceptions are reported with logs', run: async ({ call }) => {
    await assert.rejects(call('exec', { code: 'ctx.Log("before"); throw new InvalidOperationException("boom");' }), /InvalidOperationException: boom[\s\S]*before/);
  } },
  { name: 'ctx.Edit is undoable', run: async ({ call, exec }) => {
    const a = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'ExecEdit' });
    await exec(`var a = ctx.Find("${a.id}"); ctx.Edit(a, () => a.Name = "ExecRenamed");`);
    assert.equal((await call('find', { name: 'ExecRenamed' })).length, 1);
    await call('undo');
    assert.equal((await call('find', { name: 'ExecRenamed' })).length, 0);
  } },
  { name: 'unsafe pointer APIs work through exec', run: async ({ exec }) => {
    assert.equal(await exec('unsafe { int v = 41; int* p = &v; *p += 1; return v; }'), 42);
  } },
  { name: 'custom usings', run: async ({ call }) => {
    const r = await call('exec', { code: 'new StringBuilder("ok").ToString()', usings: ['System.Text'] });
    assert.equal(r.value, 'ok');
  } },
];

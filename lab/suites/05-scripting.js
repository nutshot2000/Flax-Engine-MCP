import assert from 'node:assert/strict';
export const area = 'scripting';
const probe = (extra = '') => `using FlaxEngine;
namespace Game
{
    public class LabProbe : Script
    {
        public float Speed = 1;
        public int Ticks;
        public override void OnUpdate() { Ticks++; }${extra}
    }
}
`;
async function compile(lab, source) {
  const before = (await lab.call('status')).compilationCount;
  await lab.call('exec', { code: '1' }); // ensure bridge is responsive before writing
  await lab.client.callTool({ name: 'flax_write_script', arguments: { path: 'LabProbe.cs', source, overwrite: true } });
  return lab.until(async () => { const s = await lab.call('status').catch(() => null); return s && !s.compiling && s.compilationCount > before ? s : null; }, { timeout: 120000, what: 'script compilation' });
}
export const tests = [
  { name: 'write and compile a gameplay script', run: async lab => {
    const s = await compile(lab, probe());
    assert.equal(s.compilationFailed, false);
    return `compilation #${s.compilationCount}`;
  } },
  { name: 'attach script and edit its fields', run: async ({ call }) => {
    const a = await call('spawn', { type: 'FlaxEngine.EmptyActor', name: 'ProbeHost' });
    await call('add_script', { id: a.id, type: 'Game.LabProbe', properties: { Speed: 3 } });
    await call('set', { id: a.id, script: 'Game.LabProbe', properties: { Speed: 4.5 } });
    const r = await call('get', { target: { kind: 'script', id: a.id, type: 'Game.LabProbe' }, members: ['Speed'] });
    assert.equal(r.Speed.value, 4.5);
  } },
  { name: 'exec can use game assembly types', run: async ({ exec }) => {
    assert.equal(await exec('typeof(Game.LabProbe).Assembly.GetName().Name'), 'Game.CSharp');
  } },
  { name: 'compilation failures are reported in status and logs', run: async lab => {
    const s = await compile(lab, probe('\n        void Broken() { int x = "nope"; }'));
    try {
      assert.equal(s.compilationFailed, true);
      const log = await lab.client.callTool({ name: 'flax_logs', arguments: { filter: 'error CS', lines: 20 } });
      assert.match(log.content[0].text, /CS0029/);
    } finally {
      assert.equal((await compile(lab, probe())).compilationFailed, false, 'restore compiles');
    }
  } },
];

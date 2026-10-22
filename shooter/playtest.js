// Plays the shooter through the bridge: aims at the nearest drone with the bot API and fires, recording what happens.
// Usage: FLAX_PROJECT=D:/FlaxProjects/BridgeGame node shooter/playtest.js [seconds]
import { promises as fs } from 'node:fs';
import { connect } from '../tests/client.js';
const { client, call } = await connect();
const seconds = Number(process.argv[2] ?? 60);
const wait = ms => new Promise(r => setTimeout(r, ms));
const checks = {};
const note = (name, passed, detail) => { if (!(name in checks) || (passed && !checks[name].passed)) checks[name] = { passed, detail }; };
async function shot(name) {
  const r = await client.callTool({ name: 'flax_screenshot', arguments: { view: 'game' } });
  if (r.content[0].type === 'image') await fs.writeFile(new URL(`./screenshots/${name}.png`, import.meta.url), Buffer.from(r.content[0].data, 'base64'));
}
const values = r => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, v.value ?? v.error]));
try {
  await fs.mkdir(new URL('./screenshots/', import.meta.url), { recursive: true });
  if (!(await call('status')).play) { await call('play'); for (let i = 0; i < 50 && !(await call('status')).play; i++) await wait(100); }
  await wait(500);
  const [director] = await call('find', { name: 'GameDirector' });
  const [player] = await call('find', { name: 'Player', type: 'FlaxEngine.CharacterController' });
  const game = { kind: 'script', id: director.id, type: 'Game.ShooterGame' };
  const gun = { kind: 'script', id: player.id, type: 'Game.PlayerController' };
  const state = async () => values(await call('get', { target: game, members: ['Phase', 'Wave', 'Score', 'Kills', 'EnemiesAlive', 'PendingSpawns', 'PlayerHp', 'PlayerPosition', 'SoundEvents'] }));
  await shot('title');
  await call('call', { target: game, method: 'StartGame' });
  const started = Date.now();
  let s = await state(), lastLog = 0, firstDronePos = null, firstDroneId = null;
  while (Date.now() - started < seconds * 1000) {
    s = await state();
    note('run starts', s.Phase === 'playing' || s.Phase === 'lost', s.Phase);
    if (s.Phase !== 'playing') break;
    const drones = await call('find', { name: 'Drone_', type: 'FlaxEngine.CharacterController' });
    note('wave spawns drones from prefab', drones.length > 0, `${drones.length} drones in wave ${s.Wave}`);
    if (drones.length) {
      const p = s.PlayerPosition;
      const near = drones.map(d => ({ d, dist: Math.hypot(d.position[0] - p.X, d.position[2] - p.Z) })).sort((a, b) => a.dist - b.dist)[0].d;
      if (!firstDroneId) { firstDroneId = near.id; firstDronePos = near.position; }
      else if (near.id === firstDroneId) {
        const moved = Math.hypot(near.position[0] - firstDronePos[0], near.position[2] - firstDronePos[2]);
        note('drones navigate toward the player', moved > 150, `moved ${moved.toFixed(0)} cm`);
      }
      const ai = values(await call('get', { target: { kind: 'script', id: near.id, type: 'Game.Enemy' }, members: ['PathPoints', 'CanSeePlayer'] }).catch(() => ({})));
      if (ai.PathPoints) note('enemy uses navmesh path', ai.PathPoints >= 2, `${ai.PathPoints} points`);
      await call('call', { target: gun, method: 'BotAimAt', args: [{ X: near.position[0], Y: near.position[1], Z: near.position[2] }] });
      await call('call', { target: gun, method: 'BotFire', args: [3] });
    }
    await wait(350);
    if (Date.now() - lastLog > 3000) {
      lastLog = Date.now();
      const g = values(await call('get', { target: gun, members: ['Ammo', 'ShotsFired', 'Hits', 'Reloading'] }));
      console.log(JSON.stringify({ t: ((Date.now() - started) / 1000).toFixed(0), phase: s.Phase, wave: s.Wave, kills: s.Kills, alive: s.EnemiesAlive, hp: s.PlayerHp, ammo: g.Ammo, shots: g.ShotsFired, hits: g.Hits }));
      note('rifle hits drones', g.Hits > 0, `${g.Hits}/${g.ShotsFired} hits`);
      note('magazine empties and reloads', g.ShotsFired > 24, `${g.ShotsFired} shots fired`);
      if (g.ShotsFired > 10 && !checks.combat) { await shot('combat'); checks.combat = { passed: true }; }
    }
    note('drones die and score', s.Kills > 0, `${s.Kills} kills, ${s.Score} points`);
    note('waves advance', s.Wave >= 2, `wave ${s.Wave}`);
    note('drones damage the player', s.PlayerHp < 100, `hp ${s.PlayerHp}`);
    note('sounds play', s.SoundEvents > 0, `${s.SoundEvents} events`);
  }
  await shot('end');
  const g = values(await call('get', { target: gun, members: ['ShotsFired', 'Hits'] }));
  const result = { utc: new Date(), seconds: Math.round((Date.now() - started) / 1000), final: { ...s, ...g }, checks };
  await fs.writeFile(new URL('./PLAYTEST.json', import.meta.url), JSON.stringify(result, null, 2));
  for (const [name, c] of Object.entries(checks)) console.log(`${c.passed ? 'PASS' : 'FAIL'} ${name}${c.detail ? ' — ' + c.detail : ''}`);
} finally {
  await call('stop_play').catch(() => {});
  await client.close();
}

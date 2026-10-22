# Hold the Line (shooter test game)

A first-person arena shooter built entirely through the MCP bridge, used to exercise physics raycasts, navmesh AI, prefabs, camera-space UI, audio and play mode.

- Survive endless drone waves. Drones path to you over the navmesh, keep their distance, circle-strafe and fire inaccurate shots.
- Hitscan rifle: 24-round magazine, reload, muzzle flash, tracers, impacts and hit markers.
- Controls: WASD move, Shift sprint, Space jump, mouse aim/fire, R reload, Esc release the mouse, Enter start/retry.

## Rebuilding

With an editor open on a project that has the bridge plugin (e.g. `install-project.ps1 -Create`) and a `Main.scene` loaded:

```powershell
$env:FLAX_PROJECT='D:\FlaxProjects\BridgeGame'
node shooter/push-scripts.js     # writes Source/*.cs via flax_write_script and compiles once
node shooter/build.js            # materials, sounds, arena, player, drone prefab, spawn points, director, navmesh
node shooter/build.js --rebuild  # replace an existing arena
node shooter/landscape.js        # terrain hills and pines/bushes/rocks outside the walls (--rebuild to replace)
node shooter/playtest.js 60      # bot plays for 60 s through the bridge; writes PLAYTEST.json and screenshots/
```

`ShooterGame` and `PlayerController` expose automation members used by the playtest: `StartGame()`, `BotAimAt(Vector3)`, `BotFire(shots)`, `BotMove(x, z, seconds, jump)`, `BotLook(yaw, pitch)`, plus readable state (`Phase`, `Wave`, `Kills`, `Score`, `PlayerHp`, `Ammo`, `ShotsFired`, `Hits`). Bot input runs through the same movement and firing code as keyboard and mouse.

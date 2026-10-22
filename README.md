# Flax Engine MCP

A local MCP bridge for **Flax Engine 1.13**, with an editable sample game, **Neon Relay**. Built and live-tested on Windows with Flax 1.13.7012 and .NET 10.

![Neon Relay](game/title.png)

## What it does

- Creates and edits actors, transforms, materials and scripts.
- Discovers public engine, editor and gameplay APIs; reads properties and invokes methods.
- Imports assets, creates material instances and prefabs, performs batches and editor undo/redo.
- Writes and compiles C# gameplay code, reports compilation state, reads UTF-16 editor logs.
- Controls play/pause/stop, captures viewports, and reads runtime gameplay state.
- Reads/writes project JSON assets such as game settings.

This is an early, trusted-local development bridge. Public API discovery provides broad access, but **not every engine subsystem has been exercised**. It is not a sandbox for untrusted clients or scripts.

## Quick start

Requirements: Flax 1.13, its required .NET SDK, and Node.js 20 or newer.

1. Run `npm ci` in this repository.
2. Open `examples/NeonRelay/NeonRelay.flaxproj` with Flax 1.13. The sample already includes the editor plugin.
3. Configure an MCP stdio server:

```json
{
  "mcpServers": {
    "flax": {
      "command": "node",
      "args": ["C:/path/to/Flax-Engine-MCP/src/server.js"],
      "env": {
        "FLAX_PROJECT": "C:/path/to/Flax-Engine-MCP/examples/NeonRelay",
        "FLAX_ENGINE": "C:/path/to/Flax_1.13"
      }
    }
  }
}
```

For another existing project, close its editor and run:

```powershell
.\install-project.ps1 -ProjectDirectory 'D:\FlaxProjects\MyGame' -EngineDirectory 'D:\path\to\Flax_1.13'
```

Use `-Create` to create a new project directory. Installation backs up existing changed source files. The plugin is an editor-only C# module. The source tools currently target `Source/Game`.

Add the project through **Flax Launcher → Add Project** if you want it in the launcher library. The launcher's project-list file is UTF-16; do not rewrite it as UTF-8.

## Neon Relay

Collect eight energy cores, avoid three patrolling drones, and reach the far extraction gate before the timer expires.

- Enter: start
- WASD: move
- Space: jump
- Shift: dash
- R: restart

Includes collision, health, cooldowns, pickups, generated sound effects, HUD, title/loss/win states, reusable materials and a drone prefab. Open `Content/NeonRelay.scene`, then press Play. The scene remains editable.

**Validation:** live movement, grounded collision, jumping, dash distance/cooldown, pause/resume, pickups, damage, and loss were observed. The automated route collected four cores before losing to drones. Full victory/extraction, packaging, other platforms, and cross-machine portability are not yet verified. The test is intentionally left capable of failing; it does not disable hazards or teleport the player to manufacture a pass.

## Using the bridge

Start with `flax_status`, `flax_scene_tree` and `flax_inspect`. Coordinates use centimeters. Vectors use `{ "X": 0, "Y": 100, "Z": 0 }`. C# type and member names are case-sensitive.

Use `flax_types` and `flax_api` to discover APIs. Generic targets support `actor`, `script`, `object` (any engine object by ID), `asset`, `editor` and `static`, with optional dot-separated member traversal. For overloaded calls, specify `parameterTypes` exactly as returned by API discovery. Value forms:

- references: `{ "$ref": { "kind": "actor", "id": "..." } }`
- boxed values: `{ "$type": "FlaxEngine.Color", "value": { "R": 1, "G": 0, "B": 0, "A": 1 } }`
- constructed objects: `{ "$new": "FlaxEngine.GUI.Label", "args": [] }`
- primitives convert through implicit operators, so `"Text": "Hello"` works for `LocalizedString`.

`flax_call` supports `ref`/`out` parameters (results come back in `out`, e.g. `Physics.RayCast` hits, `Navigation.FindPath` points) and generic methods via `typeArguments`. Asset paths accept `$engine/...` for engine content, `$content/...` or a bare relative path for project content, or an absolute path.

`flax_find` searches actors by name, type and tag; `flax_scene_tree` accepts `id` and `depth` for large scenes; `flax_unload_scene` closes one scene.

### Terrain, foliage and vertex paint

These cover the editor's Terrain, Foliage and Paint tabs. Brushes take a world X/Z `center`, `radius`, optional `innerRadius` (ring) and `falloff` in centimetres. They are saved with the scene but are not editor undo steps.

- `flax_terrain`: `create` (chunk size, patch grid, terrain material), `sculpt` (`raise`, `lower`, `flatten`, `set`, `smooth`, `noise`), `paint` (splat layers 0-7), `sample` (heights at world points) and `info`.
- `flax_foliage`: `create`, `add_type` (one per model, with material, cull distance and FoliageType properties), `scatter` (raycasts onto surfaces in a disc or ring, with scale range, `heightScale`, `baseOffset`, slope limit, spacing, `alignToNormal` and `onlyOn`), `clear` and `info`.
- `flax_vertex_paint`: `fill`, `gradient` along a local axis, and `clear`. Colours only show with a material that reads vertex colour.

### flax_exec

For anything the dedicated tools do not reach, `flax_exec` compiles a C# snippet with the engine's bundled Roslyn and runs it on the editor main thread against every loaded engine, editor and game assembly. A body without `;` is an expression whose value is returned:

```json
{ "code": "Level.Scenes.Sum(s => s.ChildrenCount)" }
```

Snippets run in an `unsafe` context (pointer APIs such as `TerrainTools.ModifyHeightMap` work) and receive `ctx` with `Log(x)`, `Find(id)`, `Asset<T>(path)`, `Scene`, `Editor`, and `Edit(sceneObject, action)` for undoable edits. Compile errors report snippet line numbers. A snippet can do anything the editor can, including crashing it with a bad pointer; check native results (for example null heightmap pointers) before dereferencing.

`flax_wait` and status help verify asynchronous compilation and play transitions. Source writes request compilation but do not imply compilation success. Inspect `compiling` and `compilationFailed`.

Game screenshots capture the render task. Neon Relay uses a camera-space HUD so it is included. Screen-space UI composited later by the editor may be absent from these captures.

## Reliability and limitations

Requests are written atomically to `Cache/McpBridge` and processed on the editor main thread in submission order, including parallel tool calls. One editor owns a project queue. Started/completed/failed receipts prevent automatic replay after a crash. After a timeout, inspect `flax_receipt` instead of repeating an edit. A failed batch or multi-property edit may have partially applied.

STOP persists across restarts and blocks bridge mutations until explicit resume. `flax_set`, `flax_object_set`, `flax_material`, actor creation/deletion and `ctx.Edit` in `flax_exec` are editor undo steps. Save explicitly. Scene opening is additive. Receipts and screenshots older than two days are pruned when the editor starts.

The plugin runs with the editor user's permissions. Generic API calls and generated C# are powerful; expose this server only to trusted local clients. No network listener or model API key is required.

Terrain sculpting and painting, foliage, vertex painting, navmesh building, physics queries, splines, UI, CSG and about 25 actor types are exercised by the lab suite (see [lab/COVERAGE.md](lab/COVERAGE.md)). Material graphs (including terrain materials), animation graphs, particle authoring and packaged builds are reachable only through generic calls and `flax_exec`, without dedicated tests yet. The installed test machine reported missing optional Android/Web/Vulkan/ARM64 components; Windows x64 C# compilation worked.

## Bridge lab

`lab/` is a live coverage harness for developing the bridge itself. It runs against a disposable project, never the sample game:

```powershell
.\install-project.ps1 -ProjectDirectory D:\FlaxProjects\BridgeLab -Create
$env:FLAX_PROJECT='D:\FlaxProjects\BridgeLab'
node tests/call.js flax_launch   # opens the lab project in a second editor
npm run lab                      # all suites; writes lab/COVERAGE.md and lab/results/latest.json
npm run lab -- terrain           # only tests whose area/name match
node lab/reload.js               # after plugin edits: sync, hot-reload and wait
```

Each run unloads the open scenes and creates a fresh `Content/Lab` scene. The runner refuses project paths that do not look disposable unless `FLAX_LAB_FORCE=1`. Suites live in `lab/suites/*.js`; each exports an `area` and a list of `{ name, run(lab) }` tests that use `lab.call`, `lab.exec`, `lab.until` and `lab.tool`.

## Tests and development

- `npm test`: source path, request ordering, editor discovery and plugin-copy checks.
- `npm run sync [project]`: copy `plugin/FlaxMcp` into the bundled example (and optionally another project). The bundled copy must match; `npm test` fails otherwise.
- `npm run lab`: live coverage harness (above).
- `npm run smoke`: modifies a disposable project, creating/deleting a test cube and writing `BridgeMotion.cs`. Set `FLAX_PROJECT` to a test project with an open scene.
- `node tests/gameplay.js`: Neon Relay movement-based integration test, including a complete-route attempt.
- `game/build-arena.js`: original scene-construction recipe; refuses to overwrite an existing arena.

The editor is found through `FLAX_EDITOR`, `FLAX_ENGINE` or the Flax Launcher's engine list; `install-project.ps1` uses the same discovery.

`VALIDATION.json` records the earlier bridge smoke pass. `GAMEPLAY-VALIDATION.json` records the current game's partial playtest. Generated caches, binaries, credentials and machine-local MCP configuration are excluded.

Flax Engine and its bundled resources remain subject to their own license. The repository does not include the engine installation. See [Flax documentation](https://docs.flaxengine.com/) and [MCP TypeScript SDK](https://github.com/modelcontextprotocol/typescript-sdk).

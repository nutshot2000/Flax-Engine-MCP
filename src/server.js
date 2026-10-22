import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { request, receipt, project, queue, readJson, sourcePath, delay, editorPath } from './bridge.js';
const server = new McpServer({ name: 'flax-mcp', version: '0.4.0' }, { instructions: 'Control Flax Editor. Inspect status/tree before edits. Positions in centimeters. Stop play before persistent edits. Save explicitly. Play and screenshot are asynchronous: observe completion. Never repeat an uncertain mutation; inspect its receipt. Asset paths accept "$engine/..." (engine Content), "$content/..." or a path relative to project Content. Use flax_api to discover members; flax_call handles ref/out parameters and generic methods (typeArguments). For anything the dedicated tools do not cover, flax_exec runs a C# snippet inside the editor with full engine and editor access. Scripts execute locally with editor privileges. STOP requires explicit resume_control.' });
const text = result => ({ content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] });
function tool(name, description, schema, fn) {
  server.registerTool('flax_' + name, { description, inputSchema: schema }, async args => {
    try { return await fn(args); } catch(e) { return { isError: true, content: [{ type:'text', text: e.message }] }; }
  });
}
function command(name, description, schema = {}, op = name) { tool(name, description, schema, async a => text(await request(op, a))); }
const id = z.string().uuid();
const properties = z.record(z.unknown());
const target = z.object({kind:z.enum(['actor','script','object','asset','editor','static']),id:z.string().optional().describe('actor/script/object ID'),type:z.string().optional().describe('script type or static type'),asset:z.string().optional(),member:z.string().optional().describe('dot-separated public member path')});
command('types','Discover public types by name. Engine, editor, game and plugin assemblies by default; all=true includes System/Microsoft.',{filter:z.string().default(''),all:z.boolean().default(false),limit:z.number().int().min(1).max(2000).default(200)});
command('api','Discover exact public constructors, properties, fields and method signatures (including ref/out and generic parameters) for any loaded type. member filters by name.',{type:z.string(),member:z.string().optional()});
command('get','Read public fields/properties of an actor, script, object (by ID), asset, editor or static type. member traverses dot-separated public members.',{target,members:z.array(z.string()).optional()});
command('object_set','Set public members on any engine/editor object, recorded in editor undo for scene objects. Use api first. Values: {$ref: target}, {$type: fullType, value}, {$new: fullType, args: []}.', {target,properties,runtime:z.boolean().default(false)});
command('call','Invoke a public method. ref/out results are returned in "out". Generic methods need typeArguments. Specify parameterTypes for overloads exactly as flax_api reports them. Mutating and trusted-local only. No automatic retry.',{target,method:z.string(),args:z.array(z.unknown()).default([]),parameterTypes:z.array(z.string()).optional(),typeArguments:z.array(z.string()).optional()});
tool('exec','Compile and run a C# snippet on the editor main thread with access to all loaded engine, editor and game assemblies. A body without ";" is treated as an expression and its value returned; otherwise use return. ctx offers Log(x), Find(id), Asset<T>(path), Edit(sceneObject, action) for undoable edits, Scene and Editor. Trusted-local only.',{code:z.string().max(100000),usings:z.array(z.string()).optional(),timeoutSeconds:z.number().min(1).max(120).default(30)},async a=>text(await request('exec',{code:a.code,usings:a.usings},a.timeoutSeconds*1000)));
command('batch','Execute up to 128 scene edits in order. Stops on first error and reports partial results; NOT atomic. Allowed ops: spawn,set,material,delete,select,add_script.',{operations:z.array(z.object({op:z.string(),args:properties})).max(128)});
command('material_create','Create a persistent material instance from a base material (default $engine/Engine/SingleColorMaterial.flax). Box colors as {$type:"FlaxEngine.Color",value:{R:1,G:0,B:0,A:1}}.',{path:z.string(),base:z.string().optional(),parameters:properties.default({})});
command('prefab_create','Save actor hierarchy as prefab; refuses overwrite.',{id,path:z.string()});
command('prefab_spawn','Instantiate project prefab into first loaded scene.',{path:z.string(),properties:properties.optional()});
command('import','Queue asset import from a local source file into Content subfolder. Poll editor ContentImporting.IsImporting and inspect assets/logs.',{source:z.string(),folder:z.string().default('Imported')});
command('close_scenes','Close all scenes; refuses unsaved changes.');
command('undo','Undo last editor undoable operation.');
command('redo','Redo last editor undoable operation.');
command('status', 'Get live editor state, open scenes, engine/project content folders, capabilities and STOP state.');
command('scene_tree', 'Read scene hierarchy, actor IDs, transforms and attached scripts. id roots the tree at one actor; depth limits nesting (childCount is always reported).', {id:id.optional(),depth:z.number().int().min(0).optional()});
command('find', 'Find actors in loaded scenes by case-insensitive name substring, type (including subclasses) and/or tag.', {name:z.string().optional(),type:z.string().optional(),tag:z.string().optional(),limit:z.number().int().min(1).max(1000).default(100)});
command('inspect', 'Discover public actor or script properties, their types and current values.', { id, script: z.string().optional() });
command('create_scene', 'Create and open a new default scene. Refuses overwrite. Path relative to Content.', { path: z.string().endsWith('.scene') });
command('open_scene', 'Load an existing scene additively. Inspect tree first to avoid loading the same scene twice.', {path:z.string().endsWith('.scene')});
command('unload_scene', 'Unload one loaded scene by ID. Refuses unsaved changes unless discard=true.', {id,discard:z.boolean().default(false)});
command('material', 'Assign an existing material asset to a StaticModel material slot (undoable).', {id,path:z.string(),slot:z.number().int().min(0).default(0)});
command('spawn', 'Create an actor using its fully qualified type, e.g. FlaxEngine.StaticModel. Properties use exact C# names. Vectors use {X,Y,Z}; asset references use asset paths such as "$engine/Editor/Primitives/Cube.flax".', { type: z.string(), name: z.string(), parent: id.optional(), properties: properties.optional() });
command('set', 'Set actor or attached script properties as one undoable edit. Inspect first. Changes are not automatically saved.', { id, script: z.string().optional(), properties });
command('add_script', 'Attach an already compiled Script type to an actor.', { id, type: z.string(), properties: properties.optional() });
command('delete', 'Delete an actor and its children using editor undo support.', { id });
command('select', 'Select actor in the editor.', { id });
command('camera', 'Set editor viewport properties, e.g. ViewPosition and ViewDirection.', { properties });
command('save', 'Save all loaded scenes to disk.');
command('compile', 'Request C# compilation and hot reload. Check logs and status afterwards.');
tool('wait','Wait up to 10 seconds then read fresh editor state; useful for play, compile and import completion.',{seconds:z.number().min(0).max(10).default(1)},async a=>{await delay(a.seconds*1000);return text(await request('status'));});
tool('json_asset','Read or update a project JSON asset such as GameSettings.json. Writes preserve backups. New assets require type. Paths stay beneath Content.',{path:z.string().endsWith('.json'),data:properties.optional(),type:z.string().optional()},async a=>{
 const root=path.join(project,'Content'); const dest=path.resolve(root,a.path);if(!dest.startsWith(root+path.sep))throw Error('Path escapes Content');
 let asset=await readJson(dest).catch(()=>null);
 if(!a.data){if(!asset)throw Error('Asset missing');return text(asset);}
 const state=await request('status');if(state.result.play||state.result.stopped)throw Error('Stop play and enable control before changing settings');
 if(asset)await fs.copyFile(dest,dest+'.backup-'+Date.now());else {if(!a.type)throw Error('New assets require type');asset={ID:randomUUID().replaceAll('-',''),TypeName:a.type,EngineBuild:7012,Data:{}};}
 asset.Data={...asset.Data,...a.data};await fs.mkdir(path.dirname(dest),{recursive:true});await fs.writeFile(dest,JSON.stringify(asset,null,2));return text({path:dest,asset,reload:'Flax content watcher reloads this asset; verify settings before building.'});
});
for (const [name, desc] of Object.entries({play:'Request play mode; poll status to verify.',pause:'Pause play mode.',resume_play:'Resume paused play.',stop_play:'Stop play mode; poll status to verify.',stop:'Latch STOP and request play stop. Inspection remains available.',resume_control:'Explicitly clear STOP to allow editing.'})) command(name,desc);
tool('receipt','Inspect an operation after timeout; never blindly repeat edits.',{operationId:id},async a=>text(await receipt(a.operationId)));
tool('screenshot','Capture scene or game viewport and return image; game view includes HUD.',{view:z.enum(['scene','game']).default('scene')}, async a=>{
  const r = await request('screenshot',a);
  for(let i=0;i<100;i++) { try { const data=await fs.readFile(r.result.path); if(data.length>100 && data.subarray(-8,-4).toString()==='IEND') return {content:[{type:'image',mimeType:'image/png',data:data.toString('base64')}]}; } catch {} await delay(100); }
  return text(r);
});
tool('write_script','Create/update C# gameplay source beneath Source/Game. Existing files require overwrite=true and are backed up. Flax recompiles automatically; inspect logs afterwards.',{path:z.string(),source:z.string().max(200000),overwrite:z.boolean().default(false)},async a=>{
  const state=await request('status'); if(state.result.stopped || state.result.play) throw Error('Stop play and ensure bridge control is enabled before writing scripts');
  const dest=sourcePath(a.path); await fs.mkdir(path.dirname(dest),{recursive:true});
  const exists=await fs.stat(dest).catch(()=>null);
  if(exists && !a.overwrite) throw Error('File exists; use overwrite=true');
  if(exists) await fs.copyFile(dest,dest+'.backup-'+Date.now());
  await fs.writeFile(dest,a.source); await request('compile'); return text({written:dest,recompilation:'requested; check logs and status'});
});
tool('read_script','Read C# gameplay source beneath Source/Game.',{path:z.string()},async a=>text({source:await fs.readFile(sourcePath(a.path),'utf8')}));
tool('assets','List project content files. Use relative folder and optional case-insensitive name filter.',{filter:z.string().default('')},async a=>{
 const base=path.join(project,'Content'); const files=await fs.readdir(base,{recursive:true}); return text({files:files.filter(f=>f.toLowerCase().includes(a.filter.toLowerCase())).slice(0,1000)});
});
tool('logs','Read latest editor log, including script compilation failures.',{lines:z.number().int().min(1).max(500).default(80),filter:z.string().optional().describe('case-insensitive substring; applied before taking the last lines')},async a=>{
  const dir=path.join(project,'Logs'); const files=await fs.readdir(dir);
  const stats=await Promise.all(files.map(async f=>({f,stat:await fs.stat(path.join(dir,f))})));
  stats.sort((a,b)=>b.stat.mtimeMs-a.stat.mtimeMs);
  const file=path.join(dir,stats[0].f); const bytes=await fs.readFile(file); const decoded=bytes.toString(bytes[0]===255&&bytes[1]===254?'utf16le':'utf8').replace(/\0/g,'');
  let lines=decoded.split('\n'); if(a.filter) lines=lines.filter(l=>l.toLowerCase().includes(a.filter.toLowerCase()));
  return text({file,text:lines.slice(-a.lines).join('\n')});
});
tool('launch','Launch configured Flax project in the editor.',{},async()=>{
  const existing=await readJson(path.join(queue,'status.json')).catch(()=>null);
  if(existing && Date.now()-Date.parse(existing.utc)<10000) return text({alreadyRunning:true,project});
  const pending=await readJson(path.join(project,'Cache','mcp-launch.json')).catch(()=>null);
  for(const pid of [existing?.processId,pending?.pid].filter(Boolean)){try{process.kill(pid,0);return text({alreadyRunning:true,pid,project,bridgeReady:false});}catch{}}
  const files=await fs.readdir(project); const proj=files.find(f=>f.endsWith('.flaxproj')); if(!proj) throw Error('No .flaxproj found');
  const child=spawn(editorPath(),['-project',path.join(project,proj)],{detached:true,stdio:'ignore'}); child.unref();
  await fs.mkdir(path.join(project,'Cache'),{recursive:true});await fs.writeFile(path.join(project,'Cache','mcp-launch.json'),JSON.stringify({pid:child.pid,utc:new Date().toISOString()}));
  return text({launched:true,pid:child.pid});
});
await server.connect(new StdioServerTransport());

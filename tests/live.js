import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {promises as fs} from 'node:fs';
import assert from 'node:assert/strict';
const client=new Client({name:'flax-live-validation',version:'1'});
const checks=[];
await client.connect(new StdioClientTransport({command:process.execPath,env:process.env,args:[fileURLToPath(new URL('../src/server.js',import.meta.url))]}));
const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function call(name,args={}) {const r=await client.callTool({name:'flax_'+name,arguments:args}); if(r.isError)throw Error(r.content[0].text); return JSON.parse(r.content[0].text).result;}
try {
 const list=await client.listTools(); checks.push({check:'MCP tool discovery',count:list.tools.length});
 assert.equal((await call('status')).play,false);
 const tree=await call('scene_tree'); assert(tree.length>0);
 for(const old of tree[0].children.filter(x=>x.name==='MCP Test Cube')) await call('delete',{id:old.id});
 const model=await call('spawn',{type:'FlaxEngine.StaticModel',name:'MCP Test Cube',properties:{Position:{X:0,Y:100,Z:0},Model:'$engine/Editor/Primitives/Cube.flax'}});
 await call('material',{id:model.id,path:'$engine/Engine/WhiteMaterial.flax'});
 await call('set',{id:model.id,properties:{Scale:{X:1.2,Y:1.2,Z:1.2}}});
 const updated=(await call('scene_tree'))[0].children.find(x=>x.id===model.id); assert.equal(updated.scale[0],1.2); checks.push({check:'spawn, material, transform and readback',passed:true});
 const script='using FlaxEngine; namespace Game { public class BridgeMotion : Script { public float Speed = 90; public override void OnUpdate() { Actor.LocalEulerAngles += new Vector3(0,Speed * Time.DeltaTime,0); Actor.Position += new Vector3(Time.DeltaTime * 10,0,0); } } }';
 const wr=await client.callTool({name:'flax_write_script',arguments:{path:'BridgeMotion.cs',source:script,overwrite:true}}); assert(!wr.isError);
 await wait(8000);
 await call('add_script',{id:model.id,type:'Game.BridgeMotion'}); checks.push({check:'write, compile and attach C# script',passed:true});
 await call('save');
 await call('play'); await wait(2500); assert.equal((await call('status')).play,true);
 const playing=(await call('scene_tree'))[0].children.find(x=>x.name==='MCP Test Cube'); assert(playing.position[0]>1); checks.push({check:'play mode executes generated gameplay script',position:playing.position});
 await call('pause'); await wait(300); const before=(await call('scene_tree'))[0].children.find(x=>x.name==='MCP Test Cube').position[0]; await wait(500); const after=(await call('scene_tree'))[0].children.find(x=>x.name==='MCP Test Cube').position[0]; assert.equal(before,after); checks.push({check:'pause freezes gameplay',passed:true});
 await call('stop_play'); await wait(700); assert.equal((await call('status')).play,false);
 await call('stop'); const blocked=await client.callTool({name:'flax_spawn',arguments:{type:'FlaxEngine.EmptyActor',name:'Should not spawn'}}); assert(blocked.isError); checks.push({check:'STOP blocks edits',passed:true});
 await call('resume_control');
 const disposable=await call('spawn',{type:'FlaxEngine.EmptyActor',name:'Delete test'}); await call('delete',{id:disposable.id}); assert(!(await call('scene_tree'))[0].children.some(x=>x.id===disposable.id)); checks.push({check:'delete actor verified',passed:true});
 await call('save');
 const shot=await client.callTool({name:'flax_screenshot',arguments:{}}); assert.equal(shot.content[0].type,'image'); await fs.writeFile(new URL('../viewport.png',import.meta.url),Buffer.from(shot.content[0].data,'base64')); checks.push({check:'viewport screenshot',passed:true});
 await fs.writeFile(new URL('../VALIDATION.json',import.meta.url),JSON.stringify({utc:new Date(),checks},null,2)); console.log(JSON.stringify(checks,null,2));
} finally {await client.close();}

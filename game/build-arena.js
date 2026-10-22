import {connect} from '../tests/client.js';
import {promises as fs} from 'node:fs';
import path from 'node:path';
const {client,call}=await connect();
const base='D:/FlaxProjects/NeonRelay/Content/';
const engine='D:/Program Files (x86)/Flax/Flax_1.13/Content/';
const vec=(X,Y,Z)=>({X,Y,Z});
const ids={};
async function spawn(type,name,position,scale,extra={},parent){return call('spawn',{type:'FlaxEngine.'+type,name,parent,properties:{Position:vec(...position),...(scale?{Scale:vec(...scale)}:{}),...extra}});}
async function box(name,p,s,mat,shape='Cube',parent){const a=await spawn('StaticModel',name,p,s,{Model:engine+'Editor/Primitives/'+shape+'.flax'},parent);await call('material',{id:a.id,path:base+'Materials/'+mat+'.flax'});return a;}
async function collider(name,p,size){return spawn('BoxCollider',name,p,null,{Size:vec(...size)});}
try {
 const state=await call('status');if(state.play)throw Error('Stop play first');
 let tree=await call('scene_tree'); if(!tree.length)await call('open_scene',{path:'NeonRelay.scene'});
 tree=await call('scene_tree');
 // The builder owns this dedicated scene, but is deliberately not an automatic retry command.
 if(tree[0].children.some(a=>a.name==='ArenaDeck'))throw Error('Arena exists; inspect before rebuilding');
 for(const a of tree[0].children)if(['Floor','Camera','SkyLight'].includes(a.name))await call('delete',{id:a.id});
 const colors={Deck:[.035,.055,.09],Tile:[.055,.08,.12],Edge:[.12,.2,.27],Gold:[1,.58,.045],Red:[1,.06,.18],White:[.8,.95,1],Purple:[.38,.08,.75],Dark:[.015,.02,.035]};
 const existing=await client.callTool({name:'flax_assets',arguments:{filter:'Materials'}});const assetText=existing.content[0].text;
 for(const [name,rgb] of Object.entries(colors))if(!assetText.includes(name+'.flax'))await call('material_create',{path:'Materials/'+name+'.flax',parameters:{Color:{$type:'FlaxEngine.Color',value:{R:rgb[0],G:rgb[1],B:rgb[2],A:1}}}});
 await box('ArenaDeck',[0,-70,0],[30,1.4,24],'Deck');await collider('ArenaCollision',[0,-70,0],[3000,140,2400]);
 const batches=[];
 for(let x=-1200;x<=1200;x+=600)for(let z=-900;z<=900;z+=600)batches.push({op:'spawn',args:{type:'FlaxEngine.StaticModel',name:`DeckTile_${x}_${z}`,properties:{Position:vec(x,1,z),Scale:vec(5.86,.025,5.86),Model:engine+'Editor/Primitives/Cube.flax'}}});
 const tileResult=await call('batch',{operations:batches});for(const r of tileResult.results){if(!r.ok)throw Error(r.error);await call('material',{id:r.result.id,path:base+'Materials/Tile.flax'});}
 for(const x of [-1510,1510]){await box('EdgeRail',[x,25,0],[.12,.5,24.2],'Cyan');await collider('RailCollision',[x,25,0],[20,50,2420]);}
 for(const z of [-1210,1210]){await box('EdgeRail',[0,25,z],[30.2,.5,.12],'Cyan');await collider('RailCollision',[0,25,z],[3020,50,20]);}
 for(const x of [-1350,1350])for(const z of [-1050,1050]){await box('Support',[x,-360,z],[1.2,6,1.2],'Edge');await box('SignalPillar',[x,190,z],[.65,3.8,.65],'Dark');await box('PillarLight',[x,370,z],[.8,.25,.8],'Cyan');}
 for(const [x,z] of [[-450,-300],[400,200],[-500,550]]){await box('JumpBlock',[x,55,z],[2.5,1.1,2.5],'Edge');await collider('BlockCollision',[x,55,z],[250,110,250]);await box('BlockStripe',[x,112,z],[2.5,.04,.22],'Gold');}
 const positions=[[-1050,-700],[-1100,250],[-650,850],[0,600],[750,850],[1100,150],[850,-700],[0,-80]];
 for(let i=0;i<positions.length;i++){const [x,z]=positions[i];await box('CorePlinth',[x,12,z],[1.6,.24,1.6],'Dark');await box('CoreRing',[x,27,z],[1.3,.08,1.3],'Cyan');const core=await box('Core_'+i,[x,140,z],[.5,.75,.5],'Gold');await call('set',{id:core.id,properties:{LocalEulerAngles:vec(25,45,20)}});}
 const dronePositions=[[-600,100,-200],[600,100,300],[0,100,750]];
 for(let i=0;i<dronePositions.length;i++){const p=dronePositions[i];const drone=await box('Drone_'+i,p,[1,1,1],'Red','Sphere');ids['drone'+i]=drone.id;}
 await box('GateLeft',[-210,150,1050],[.5,3,.5],'Cyan');await box('GateRight',[210,150,1050],[.5,3,.5],'Cyan');await box('GateTop',[0,310,1050],[4.7,.35,.5],'Cyan');await box('GatePad',[0,8,1000],[4,.12,2.4],'Purple');
 await box('StartPad',[0,5,-900],[2.4,.08,2.4],'Cyan');
 const runner=await spawn('CharacterController','Runner',[0,100,-900],null,{Radius:35,Height:90,StepOffset:25,AutoGravity:false});ids.runner=runner.id;
 const body=await box('RunnerBody',[0,100,-900],[.62,.75,.46],'White','Cube',runner.id);await call('set',{id:body.id,properties:{LocalPosition:vec(0,0,0)}});
 const visor=await box('RunnerVisor',[0,100,-900],[.5,.16,.12],'Cyan','Cube',body.id);await call('set',{id:visor.id,properties:{LocalPosition:vec(0,20,55)}});
 const pack=await box('RunnerPack',[0,100,-900],[.3,.55,.2],'Purple','Cube',body.id);await call('set',{id:pack.id,properties:{LocalPosition:vec(0,0,-65)}});
 const camera=await spawn('Camera','ArenaCamera',[0,2450,-2600],null,{FieldOfView:56,LocalEulerAngles:vec(43.3,0,0),FarPlane:20000});ids.camera=camera.id;
 await spawn('AudioListener','Listener',[0,2450,-2600],null,{},camera.id);
 const logic=await spawn('EmptyActor','GameDirector',[0,0,0],null);ids.director=logic.id;
 await call('add_script',{id:logic.id,type:'Game.NeonRelay'});
 const sun=tree[0].children.find(x=>x.name==='Sun');if(sun)await call('set',{id:sun.id,properties:{Brightness:3,LocalEulerAngles:vec(55,-35,0)}});
 await spawn('SkyLight','Ambient',[0,500,0],null,{Brightness:1.5});
 await call('prefab_create',{id:ids.drone0,path:'Prefabs/SecurityDrone.prefab'});
 await call('camera',{properties:{ViewPosition:vec(1900,2100,-2500),ViewDirection:vec(-.5,-.55,.66)}});
 await call('save');
 await fs.writeFile(new URL('./scene-ids.json',import.meta.url),JSON.stringify(ids,null,2));
 console.log(JSON.stringify({built:true,ids}));
}finally{await client.close();}

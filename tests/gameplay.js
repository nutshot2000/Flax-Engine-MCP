import {connect} from './client.js';
import {promises as fs} from 'node:fs';
import assert from 'node:assert/strict';
const {client,call}=await connect();
const initialTree=await call('scene_tree');
const director=initialTree.flatMap(s=>s.children).find(a=>a.name==='GameDirector');
if(!director)throw Error('Open the NeonRelay scene before running this test');
const target={kind:'script',id:director.id,type:'Game.NeonRelay'};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
const checks=[];
const invoke=(method,args=[])=>call('call',{target,method,args});
async function state(){const r=await call('get',{target,members:['Phase','PlayerPosition','Grounded','Health','Score','Remaining','Elapsed','DashCooldown','SoundEvents']});return Object.fromEntries(Object.entries(r).map(([k,v])=>[k,v.value]));}
async function shot(name){const r=await client.callTool({name:'flax_screenshot',arguments:{view:'game'}});assert.equal(r.content[0].type,'image');await fs.writeFile(new URL('../game/'+name+'.png',import.meta.url),Buffer.from(r.content[0].data,'base64'));}
async function moveTo(x,z){
 for(let i=0;i<90;i++){
  const s=await state();if(s.Phase!=='running')throw Error('Run ended: '+JSON.stringify(s));
  const dx=x-s.PlayerPosition.X,dz=z-s.PlayerPosition.Z,d=Math.hypot(dx,dz);if(d<65)return s;
  const duration=Math.min(.3,d/420);
  await invoke('Drive',[dx/d,dz/d,duration,false,false]);await wait(duration*1000+70);
 }
 throw Error('Could not reach '+x+','+z);
}
try{
 let s=await call('status');assert(!s.compiling&&!s.compilationFailed);
 if(!s.play){await call('play');await wait(800);}
 await shot('title');await invoke('StartRun');await wait(500);
 s=await state();assert(s.Grounded);checks.push({check:'player settles on arena collider',passed:true,position:s.PlayerPosition});
 await invoke('Drive',[0,0,.2,true,false]);await wait(200);s=await state();assert(s.PlayerPosition.Y>100);checks.push({check:'jump lifts character',passed:true,height:s.PlayerPosition.Y});await wait(900);
 const start=(await state()).PlayerPosition.X;await invoke('Drive',[1,0,.25,false,true]);await wait(300);s=await state();assert(s.PlayerPosition.X-start>170&&s.DashCooldown>0);checks.push({check:'dash distance and cooldown',passed:true,distance:s.PlayerPosition.X-start});
 await invoke('StartRun');await wait(250);
 await call('pause');await wait(200);const frozen=(await state()).Elapsed;await wait(300);assert.equal((await state()).Elapsed,frozen);await call('resume_play');checks.push({check:'pause and resume',passed:true});
 for(const [x,z] of [[-1050,-700],[-1100,250],[-650,850],[0,600],[750,850],[1100,150],[850,-700],[0,-700],[0,-80]]){
  s=await moveTo(x,z);console.log(JSON.stringify({waypoint:[x,z],score:s.Score,health:s.Health,time:s.Elapsed}));
 }
 s=await state();assert.equal(s.Score,8);assert(s.SoundEvents>=8);checks.push({check:'collect all eight cores using movement',passed:true,health:s.Health,time:s.Elapsed,soundEvents:s.SoundEvents});await shot('gameplay');
 // Approach extraction from the unobstructed center lane.
 try{await moveTo(0,1000);}catch(e){if((await state()).Phase!=='won')throw e;}
 s=await state();assert.equal(s.Phase,'won');checks.push({check:'extraction produces win screen',passed:true,time:s.Elapsed});await shot('victory');
 await invoke('StartRun');await wait(250);s=await state();assert.equal(s.Score,0);assert.equal(s.Health,3);assert(s.Remaining>88);checks.push({check:'restart resets score health timer',passed:true});
 // Remain in a hazard patrol lane to exercise damage through gameplay.
 await moveTo(0,750);await wait(2200);s=await state();checks.push({check:'hazard contact observed',health:s.Health,passed:s.Health<3});
 await fs.writeFile(new URL('../GAMEPLAY-VALIDATION.json',import.meta.url),JSON.stringify({utc:new Date(),checks},null,2));
 console.log(JSON.stringify(checks,null,2));
}finally{await client.close();}

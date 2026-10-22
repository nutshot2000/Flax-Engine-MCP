import {connect} from '../tests/client.js';
import {promises as fs} from 'node:fs';
import path from 'node:path';
import {project} from '../src/bridge.js';
const {client,call}=await connect();
try {
 const tree=await call('scene_tree');const actors=tree[0].children;
 const ambient=actors.find(a=>a.name==='Ambient');
 await call('set',{id:ambient.id,properties:{Mode:'CustomTexture',CustomTexture:'$engine/Editor/SimplySky.flax',Brightness:3}});
 const sun=actors.find(a=>a.name==='Sun');await call('set',{id:sun.id,properties:{Brightness:5}});
 for(const [name,rgb] of Object.entries({Deck:[.09,.14,.22],Tile:[.14,.21,.30],Edge:[.24,.34,.44],Cyan:[.05,.95,1],Gold:[1,.72,.1]})){
  const target={kind:'asset',asset:'Materials/'+name+'.flax'};
  await call('call',{target,method:'SetParameterValue',args:['Color',{$type:'FlaxEngine.Color',value:{R:rgb[0],G:rgb[1],B:rgb[2],A:1}},true],parameterTypes:['System.String','System.Object','System.Boolean']});
  await call('call',{target,method:'Save',args:[],parameterTypes:['System.String']});
 }
 const scene=JSON.parse(await fs.readFile(path.join(project,'Content','NeonRelay.scene'),'utf8'));
 const r=await client.callTool({name:'flax_json_asset',arguments:{path:'GameSettings.json',type:'FlaxEditor.Content.Settings.GameSettings',data:{ProductName:'Neon Relay',CompanyName:'Local Games',FirstScene:scene.ID,NoSplashScreen:true}}});
 if(r.isError)throw Error(r.content[0].text);
 await call('save');console.log('Lighting, material colors and game settings saved');
}finally{await client.close();}

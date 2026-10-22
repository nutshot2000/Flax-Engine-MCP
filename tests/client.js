import {Client} from '@modelcontextprotocol/sdk/client/index.js';
import {StdioClientTransport} from '@modelcontextprotocol/sdk/client/stdio.js';
import {fileURLToPath} from 'node:url';
import {project} from '../src/bridge.js';
export async function connect(){
 const client=new Client({name:'flax-builder',version:'0.2'});
 await client.connect(new StdioClientTransport({command:process.execPath,env:{...process.env,FLAX_PROJECT:project},args:[fileURLToPath(new URL('../src/server.js',import.meta.url))]}));
 return {client,call:async(name,args={})=>{const r=await client.callTool({name:'flax_'+name,arguments:args});if(r.isError)throw Error(r.content[0].text);return JSON.parse(r.content[0].text).result;}};
}

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
const client = new Client({name:'flax-check',version:'1.0'});
await client.connect(new StdioClientTransport({command:process.execPath,env:process.env,args:[new URL('../src/server.js',import.meta.url).pathname.replace(/^\/([A-Za-z]:)/,'$1')]}));
try {
const result=process.argv[2] === 'list' ? await client.listTools() : await client.callTool({name:process.argv[2],arguments:JSON.parse(process.argv[3]||'{}')});
if(result.content?.some(x=>x.type==='image')) {
 const fs=await import('node:fs/promises'); const file=new URL('../viewport.png',import.meta.url); await fs.writeFile(file,Buffer.from(result.content.find(x=>x.type==='image').data,'base64')); console.log('Saved viewport.png');
} else console.log(JSON.stringify(result,null,2));
} finally {await client.close();}

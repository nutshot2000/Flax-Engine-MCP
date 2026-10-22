import {promises as fs} from 'node:fs';
import {fileURLToPath} from 'node:url';
import {connect} from '../tests/client.js';
const dir=new URL('./audio/',import.meta.url);await fs.mkdir(dir,{recursive:true});
const sounds={collect:[660,990,.19],jump:[240,520,.18],dash:[700,160,.18],hurt:[140,65,.3],win:[440,1320,.65]};
const {client,call}=await connect();
try {for(const [name,[start,end,dur]] of Object.entries(sounds)){
 const rate=44100,n=Math.floor(rate*dur),wav=Buffer.alloc(44+n*2);wav.write('RIFF');wav.writeUInt32LE(36+n*2,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(rate,24);wav.writeUInt32LE(rate*2,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(n*2,40);
 let phase=0;for(let i=0;i<n;i++){const t=i/n;phase+=2*Math.PI*(start+(end-start)*t)/rate;const env=Math.min(1,t*25)*Math.pow(1-t,1.4);wav.writeInt16LE(Math.floor(Math.sin(phase)*env*12000),44+i*2);}
 const file=new URL(name+'.wav',dir);await fs.writeFile(file,wav);await call('import',{source:fileURLToPath(file),folder:'Audio'});console.log('Imported '+name);
}}finally{await client.close();}

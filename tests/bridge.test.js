import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync,readdirSync} from 'node:fs';
import {sourcePath,receipt,requestName,editorPath} from '../src/bridge.js';
test('source writes are constrained to gameplay C# files',()=>{
 assert(sourcePath('Player.cs').endsWith('Player.cs'));
 for(const p of ['../../config.toml','../FlaxMcp/Bridge.cs','Game.Build.cs','bad.txt']) assert.throws(()=>sourcePath(p));
});
test('operation lookup rejects traversal',async()=>{await assert.rejects(receipt('../../secret'));});
test('request file names sort in submission order',()=>{
 const names=[requestName('b',1000),requestName('a',1000),requestName('c',999),requestName('d',10000)];
 assert.deepEqual([...names].sort(),[names[2],names[0],names[1],names[3]]);
});
test('editor path prefers explicit settings',()=>{
 assert.equal(editorPath({FLAX_EDITOR:'X:/e.exe'}),'X:/e.exe');
 assert.match(editorPath({FLAX_ENGINE:'X:/Flax'}),/Binaries[\\/]Editor[\\/]Win64[\\/]Release[\\/]FlaxEditor\.exe$/);
});
test('bundled example carries the current plugin',()=>{
 const dir=new URL('../plugin/FlaxMcp/',import.meta.url), copy=new URL('../examples/NeonRelay/Source/FlaxMcp/',import.meta.url);
 for(const f of readdirSync(dir)) assert.equal(readFileSync(new URL(f,copy),'utf8'),readFileSync(new URL(f,dir),'utf8'),`${f} differs; run npm run sync`);
});

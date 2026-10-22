import {test} from 'node:test';
import assert from 'node:assert/strict';
import {sourcePath,receipt} from '../src/bridge.js';
test('source writes are constrained to gameplay C# files',()=>{
 assert(sourcePath('Player.cs').endsWith('Player.cs'));
 for(const p of ['../../config.toml','../FlaxMcp/Bridge.cs','Game.Build.cs','bad.txt']) assert.throws(()=>sourcePath(p));
});
test('operation lookup rejects traversal',async()=>{await assert.rejects(receipt('../../secret'));});

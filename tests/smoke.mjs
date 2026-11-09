import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {resolve,dirname,join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {verifyAnalyzers} from './analyzers.mjs';
import {verifyContracts} from './contracts.mjs';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'..');
function run(executable,args){const result=spawnSync(executable,args,{cwd:root,stdio:'inherit'});assert.equal(result.status,0,`${executable} failed`);}
run(join(root,'.dotnet/dotnet'),['build','BackRazor.slnx','--nologo']);
run(join(root,'.dotnet/dotnet'),['run','--project','tests/BackRazor.Tests','--no-build']);
run(process.execPath,['tests/renderer-lifetime.mjs']);
await verifyAnalyzers(root);
await verifyContracts(root);
run(process.execPath,['tests/edge.mjs']);

import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, writeFile, readFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { once } from 'node:events';
import { chromium } from 'playwright';
import mqtt from 'mqtt';
import net from 'node:net';
import payloads from 'sparkplug-payload';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const native = process.env.BACKRAZOR_NATIVE && resolve(process.env.BACKRAZOR_NATIVE);
const temp = await mkdtemp(join(tmpdir(), 'backrazor-edge-'));
const container = `backrazor-test-${process.pid}`;
const codec = payloads.get('spBv1.0');
const docker = (...args) => {
  const result = spawnSync('docker', args, { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr); return result.stdout.trim();
};
const pause = ms => new Promise(r => setTimeout(r, ms));
async function until(check, message, timeout = 20000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) { if (await check()) return; await pause(50); }
  throw new Error(`Timeout: ${message}\n${logs.slice(-6000)}`);
}
// Parse the displayed snapshot only in tests; Observer never inserts it into the page DOM.
async function observed(page, selector, attribute) {
  return page.evaluate(({selector, attribute}) => {
    const text = document.querySelector('pre:not([role])')?.textContent ?? '';
    const snapshot = new DOMParser().parseFromString(text, 'text/html');
    return attribute ? snapshot.querySelector(selector)?.getAttribute(attribute) ?? null
      : snapshot.querySelectorAll(selector).length;
  }, {selector, attribute});
}
const waitDevice = (page, id) => until(() => observed(page, `[data-device-id="${id}"]`), `observed device ${id}`);
let app, browser, monitor, logs = '', messages = [], url;
const packets = (kind, id) => messages.filter(m => m.kind === kind && (id === undefined || m.id === id));
const metric = (m, name) => m.payload.metrics.find(v => v.name === name)?.value;
let configuration;
const configPath = join(temp, 'node.json');
async function configure(devices) { configuration.devices = devices; await writeFile(configPath, JSON.stringify(configuration)); }
const errors = [];
try {
  await writeFile(join(temp, 'mosquitto.conf'), 'listener 1883\nallow_anonymous true\npersistence false\n');
  const reservation = net.createServer();
  reservation.listen(0,'127.0.0.1'); await once(reservation,'listening');
  const port = reservation.address().port; await new Promise(r=>reservation.close(r));
  docker('run', '--rm', '-d', '--name', container, '-p', `127.0.0.1:${port}:1883`, '-v', `${temp}/mosquitto.conf:/mosquitto/config/mosquitto.conf:ro`, 'eclipse-mosquitto:2');
  monitor = mqtt.connect(`mqtt://127.0.0.1:${port}`, { reconnectPeriod: 200 });
  monitor.on('error', () => {});
  monitor.on('message', (topic, data, packet) => {
    const [, , kind, , id] = topic.split('/');
    messages.push({ kind, id, payload: codec.decodePayload(data), qos: packet.qos, retain: packet.retain, time: Date.now() });
  });
  await once(monitor, 'connect');
  await monitor.subscribeAsync('spBv1.0/test/#', { qos: 1 });
  configuration = { broker: { host: '127.0.0.1', port, groupId: 'test', nodeId: `node-${process.pid}` }, devices: [] };
  const a = { id: 'a', intervalMs: 100, stopDelayMs: 500 }, b = { id: 'b', intervalMs: 120 }, c = { id: 'c', intervalMs: 100 };
  await configure([a,b]);
  const project = join(root, 'examples/BackRazor.EdgeNode');
  const executable = native || join(root, '.dotnet/dotnet');
  const args = native ? [] : [join(project, 'bin/Debug/net11.0/BackRazor.EdgeNode.dll')];
  app = spawn(executable, [...args, '--config', configPath, '--state-dir', join(temp,'state'), '--urls', 'http://127.0.0.1:0'], { cwd: native ? dirname(native) : project, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production' } });
  app.stdout.on('data', data => { logs += data; url ||= logs.match(/Now listening on: (http:\/\/\S+)/)?.[1]; });
  app.stderr.on('data', data => { logs += data; });
  await until(() => url && packets('DDATA', 'b').length > 1, 'initial births and measurements');
  assert.equal(Number(packets('NBIRTH')[0].payload.seq), 0);
  assert.equal(packets('NBIRTH')[0].qos, 0);
  for (const id of ['a','b']) assert.ok(messages.indexOf(packets('DBIRTH',id)[0]) < messages.indexOf(packets('DDATA',id)[0]));
  browser = await chromium.launch({headless:true});
  const contexts = await Promise.all([browser.newContext(),browser.newContext()]);
  const pages = await Promise.all(contexts.map(context => context.newPage()));
  for (const page of pages) {
    page.on('pageerror', e => errors.push(e.message));
    page.on('console', m => {if (m.type()==='error') errors.push(m.text());});
    await page.goto(url); await waitDevice(page,'b');
    assert.equal(await page.locator('[data-device-id], [data-runtime]').count(),0);
    const text = await page.locator('pre:not([role])').textContent();
    assert.match(text, /<pre data-device-id=/);
    assert.match(text, /\n[ \t]+<pre>/);
    assert.match(text, /\n[ \t]+samples: \d+\n/);
    assert.match(text, /^<div[^\n]+>\n  <pre>\n    node:/);
    assert.match(text, /\n  <pre data-device-id=/);
    assert.ok(!text.includes('\n\n'));
    await mkdir(join(root,'artifacts'),{recursive:true});
    await writeFile(join(root,`artifacts/snapshot-${native?'native':'jit'}.html`),text);
    assert.equal(await observed(page,'[data-runtime]','data-runtime'), native ? 'native-aot' : 'jit');
  }
  const identity = (page,id) => observed(page,`[data-device-id="${id}"]`,'data-instance');
  const originalB = await identity(pages[0],'b');
  assert.equal(originalB, await identity(pages[1],'b'));
  const originalNode = await observed(pages[0],'[data-node-instance]','data-node-instance');
  await configure([b,c]);
  await until(() => packets('DBIRTH','c').length, 'new device');
  assert.ok(messages.indexOf(packets('DDEATH','a')[0]) < messages.indexOf(packets('DBIRTH','c')[0]));
  await waitDevice(pages[0],'c');
  assert.equal(await identity(pages[0],'b'), originalB);
  assert.equal(packets('DBIRTH','b').length, 1);
  console.log('PASS: real MQTT DDEATH precedes replacement DBIRTH; equal devices and independent observers retain identity');

  const oldC = await identity(pages[0],'c');
  const changedC = {...c, intervalMs: 180};
  await configure([changedC,b]);
  await until(() => packets('DBIRTH','c').length === 2, 'same-id changed configuration');
  await until(async () => (await identity(pages[0],'c')) !== oldC, 'new same-id instance');
  assert.ok(messages.indexOf(packets('DDEATH','c')[0]) < messages.indexOf(packets('DBIRTH','c')[1]));
  await configure([b,changedC]);
  await until(async () => (await observed(pages[0],'[data-device-id]','data-device-id')) === 'b', 'reorder');
  assert.equal(await identity(pages[0],'b'), originalB);
  const beforeInvalid = messages.length;
  await configure([b,b]);
  await until(async () => (await fetch(`${url}/healthz`)).status === 503, 'duplicate rejected');
  assert.equal(await identity(pages[0],'b'), originalB);
  assert.equal(messages.slice(beforeInvalid).filter(m => ['DBIRTH','DDEATH'].includes(m.kind)).length,0);
  await configure([b,changedC]);
  await until(async () => (await fetch(`${url}/healthz`)).ok, 'valid configuration recovery');
  console.log('PASS: same-id changes dispose/recreate; reorder retains objects; invalid configuration preserves running devices');

  const topic = kind => `spBv1.0/test/${kind}/${configuration.broker.nodeId}`;
  const samples = () => Number(packets('DDATA','b').at(-1).payload.metrics.find(m=>m.name==='Samples').value);
  await until(()=>samples()>8,'samples before reset');
  await monitor.publishAsync(`${topic('DCMD')}/b`, codec.encodePayload({ metrics:[{name:'Device Control/Reset',type:'Boolean',value:true}] }));
  await until(()=>samples()<8,'device command reset');
  const births = packets('NBIRTH').length;
  await monitor.publishAsync(topic('NCMD'), codec.encodePayload({metrics:[{name:'Node Control/Rebirth',type:'Boolean',value:true}]}));
  await until(()=>packets('NBIRTH').length>births && packets('DBIRTH','b').length===2,'rebirth');
  const rebirthIndex = messages.indexOf(packets('NBIRTH').at(-1));
  assert.equal(Number(messages[rebirthIndex].payload.seq),0);
  const afterBirth = messages.slice(rebirthIndex+1).filter(m=>m.id==='b');
  assert.equal(afterBirth[0].kind,'DBIRTH');
  assert.equal(await identity(pages[0],'b'),originalB);
  console.log('PASS: DCMD changes the owning component; NCMD resets sequence and rebirths existing devices');

  for (const page of pages) assert.equal(await page.getByRole('button').count(),0);
  for (const context of contexts) await context.close();
  await configure([b,{id:'e',intervalMs:100}]);
  await until(()=>packets('DBIRTH','e').length,'configuration without circuits');
  const page=await browser.newPage();
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await waitDevice(page,'e');
  assert.equal(await identity(page,'b'),originalB);
  assert.equal(await observed(page,'[data-node-instance]','data-node-instance'),originalNode);
  console.log('PASS: operation and configuration continue with no observers and with all browser circuits closed');

  const bdSeq=Number(metric(packets('NBIRTH').at(-1),'bdSeq'));
  const birthCount=packets('NBIRTH').length;
  docker('restart',container);
  await until(()=>packets('NBIRTH').length>birthCount && Number(metric(packets('NBIRTH').at(-1),'bdSeq'))!==bdSeq,'broker reconnect',30000);
  await until(()=>packets('DBIRTH','e').length>1,'device rebirth after reconnect');
  assert.equal(Number(packets('NBIRTH').at(-1).payload.seq),0);
  assert.equal(await identity(page,'b'),originalB);
  console.log('PASS: broker restart creates a new MQTT session and bdSeq while preserving Razor device instances');

  assert.equal((await fetch(url,{headers:{Origin:'http://foreign.invalid'}})).status,403);
  await configure([]);
  await until(()=>packets('DDEATH','b').length && packets('DDEATH','e').length,'empty config disposal');
  await until(async () => (await observed(page,'[data-device-id]'))===0,'empty observed configuration');
  await configure([b]);
  await waitDevice(page,'b');
  assert.notEqual(await identity(page,'b'),originalB);
  await until(()=>packets('DBIRTH','b').length>=4,'readded birth');
  app.kill('SIGINT');
  const [exit]=await once(app,'exit');
  assert.equal(exit,0,logs);
  await until(()=>packets('NDEATH').length,'node death');
  const death=packets('NDEATH').at(-1);
  assert.equal(death.qos,1);
  assert.equal(death.payload.seq,undefined);
  assert.equal(death.payload.timestamp,undefined);
  assert.ok(messages.indexOf(packets('DDEATH','b').at(-1))<messages.indexOf(death));
  for(const m of messages.filter(m=>['NBIRTH','DBIRTH','DDATA','DDEATH'].includes(m.kind))){ assert.equal(m.qos,0);assert.equal(m.retain,false); }
  const previousBdSeq=Number(metric(packets('NBIRTH').at(-1),'bdSeq'));
  const previousBirths=packets('NBIRTH').length;
  const previousDeaths=packets('NDEATH').length;
  app=spawn(executable,[...args,'--config',configPath,'--state-dir',join(temp,'state'),'--urls','http://127.0.0.1:0'],{cwd:native?dirname(native):project});
  app.stdout.on('data',data=>{logs+=data;});app.stderr.on('data',data=>{logs+=data;});
  await until(()=>packets('NBIRTH').length>previousBirths,'process restart birth');
  assert.notEqual(Number(metric(packets('NBIRTH').at(-1),'bdSeq')),previousBdSeq);
  app.kill('SIGKILL');await once(app,'exit');
  await until(()=>packets('NDEATH').length>previousDeaths,'broker last will after process crash');
  const will=packets('NDEATH').at(-1);
  assert.equal(will.qos,1);assert.equal(will.payload.seq,undefined);assert.equal(will.payload.timestamp,undefined);
  assert.equal(Number(metric(will,'bdSeq')),Number(metric(packets('NBIRTH').at(-1),'bdSeq')));
  console.log('PASS: bdSeq survives process restart; abrupt process death produces a matching broker Last Will');
  assert.deepEqual(errors,[]);
  console.log(`PASS: ${native?'NativeAOT':'JIT'} browser circuits, empty/readd transitions, child-before-node shutdown, Sparkplug QoS and death fields`);
} finally {
  if (app && app.exitCode===null && app.signalCode===null) { app.kill('SIGINT'); await Promise.race([once(app,'exit'),pause(10000)]); if(app.exitCode===null && app.signalCode===null)app.kill('SIGKILL'); }
  await browser?.close();
  await monitor?.endAsync(true);
  spawnSync('docker',['rm','-f',container],{encoding:'utf8'});
  await mkdir(join(root,'artifacts'),{recursive:true});
  await writeFile(join(root,`artifacts/edge-${native?'native':'jit'}.log`),logs);
  await rm(temp,{recursive:true,force:true});
}

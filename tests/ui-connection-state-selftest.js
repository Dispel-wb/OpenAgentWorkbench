const fs = require('fs');
const vm = require('vm');
const path = require('path');

let definition;
global.Vue = {
  createApp(value) { definition = value; return { config: {}, mount() {} }; },
  nextTick(callback) { if (callback) callback(); return Promise.resolve(); }
};
global.window = { addEventListener() {}, removeEventListener() {}, getSelection() { return null; } };
global.document = { querySelector() { return null; }, createElement() { return {}; } };
global.crypto = { randomUUID() { return '00000000-0000-4000-8000-000000000000'; } };
vm.runInThisContext(fs.readFileSync(path.resolve(__dirname, '..', 'static', 'app.js'), 'utf8'), { filename: 'app.js' });
if (!definition) throw new Error('Vue application contract not captured');

const app = { ...definition.data(), ...definition.methods };
const ok = data => ({ ok: true, status: 200, async json() { return data; } });

async function main() {
  global.fetch = async () => { throw new TypeError('fixture disconnect'); };
  await app.api('/api/bootstrap', { timeoutMs: 1200 }).catch(() => {});
  if (app.connectionState !== 'reconnecting' || app.status !== 'Agent Host 连接中断，正在自动重连…') {
    throw new Error('Ordinary API failure did not expose the reconnecting state');
  }

  global.fetch = async () => ok({ restored: true });
  const restored = await app.api('/api/bootstrap', { timeoutMs: 1200 });
  if (!restored.restored || app.connectionState !== 'connected' || app.status !== 'Agent Host 已重新连接，后台状态已同步') {
    throw new Error('Successful API request did not clear the stale disconnect status');
  }

  app.connectionState = 'connected';
  app.status = '正在生成';
  global.fetch = async () => { throw new TypeError('fixture activity disconnect'); };
  const reusableOptions = { timeoutMs: 1200, deferConnectionFailure: true };
  await app.api('/api/workbench/activity', reusableOptions).catch(() => {});
  if (app.connectionState !== 'connected' || app.status !== '正在生成') {
    throw new Error('A single deferred activity failure caused a false global disconnect');
  }
  if (reusableOptions.timeoutMs !== 1200 || reusableOptions.deferConnectionFailure !== true) {
    throw new Error('api() mutated the caller-owned request options');
  }

  process.stdout.write(JSON.stringify({
    uiConnectionState: 'PASS',
    staleStatusCleared: true,
    deferredActivityFailure: true,
    callerOptionsPreserved: true
  }, null, 2));
}

main().catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });

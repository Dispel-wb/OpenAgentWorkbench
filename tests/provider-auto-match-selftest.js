const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');

let definition;
global.Vue = {
  createApp(value) { definition = value; return { config:{}, mount(){} }; },
  nextTick(callback) { if (callback) callback(); return Promise.resolve(); }
};
global.window = { addEventListener(){}, removeEventListener(){}, getSelection(){ return null; } };
global.document = { querySelector(){ return null; }, createElement(){ return {}; }, documentElement:{dataset:{}} };
global.crypto = { randomUUID(){ return '00000000-0000-4000-8000-000000000000'; } };
global.HTMLElement = class {};

const root = path.resolve(__dirname, '..');
for (const name of ['ui-store','provider-catalog','document-ui','workflow-ui'])
  vm.runInThisContext(fs.readFileSync(path.join(root, 'static', 'modules', name + '.js'), 'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(root, 'static', 'app.js'), 'utf8'));

async function main() {
  const app = { ...definition.data(), ...definition.methods };
  app.providerForm = blankProvider();
  app.providerForm.token = 'fixture-secret';
  app.providerForm.preset = 'siliconflow';
  app.providerForm.name = 'SiliconFlow';
  app.providerForm.text.baseUrl = 'https://api.siliconflow.cn/v1';
  const attempted = [];
  app.loadProviderHealth = async () => [];
  app.api = async (route, options) => {
    assert.equal(route, '/api/providers/probe');
    assert.equal(options.body.probeOnly, true);
    attempted.push(options.body.preset);
    if (options.body.preset === 'siliconflow') throw new Error('HTTP 401');
    if (options.body.preset !== 'deepseek') throw new Error('unexpected provider order');
    return {
      preset:'deepseek', name:'DeepSeek', authStyle:'bearer',
      text:{enabled:true,protocol:'anthropic',baseUrl:'https://api.deepseek.com/anthropic',models:['deepseek-chat']},
      image:{enabled:false,protocol:'openai-images',baseUrl:'',models:[]},
      capabilities:{schemaVersion:2,models:{'deepseek-chat':{chat:true}},evidencePolicy:'endpoint'}
    };
  };
  await app.autoConfigureProvider();
  assert.deepEqual(attempted, ['siliconflow','deepseek']);
  assert.equal(app.providerForm.preset, 'deepseek');
  assert.equal(app.providerForm.name, 'DeepSeek');
  assert.equal(app.providerForm.text.modelsText, 'deepseek-chat');
  assert.match(app.discoverMessage, /已自动匹配 DeepSeek/);
  assert.equal(app.providerError, '');
  console.log(JSON.stringify({providerAutoMatch:'PASS',wrongDefaultIgnored:true,attempted}));
}

main().catch(error => { console.error(error); process.exitCode = 1; });

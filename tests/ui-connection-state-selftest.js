const fs = require('fs');
const vm = require('vm');
const assert = require('assert/strict');
const path = require('path');

let definition;
global.Vue = {
  createApp(value) { definition = value; return { config: {}, mount() {} }; },
  nextTick(callback) { if (callback) callback(); return Promise.resolve(); }
};
global.window = { addEventListener() {}, removeEventListener() {}, getSelection() { return null; } };
global.document = { querySelector() { return null; }, createElement() { return {}; } };
global.crypto = { randomUUID() { return '00000000-0000-4000-8000-000000000000'; } };
for(const name of ['ui-store','provider-catalog','document-ui','workflow-ui'])
  vm.runInThisContext(fs.readFileSync(path.resolve(__dirname,'..','static','modules',name+'.js'),'utf8'),{filename:name+'.js'});
vm.runInThisContext(fs.readFileSync(path.resolve(__dirname, '..', 'static', 'app.js'), 'utf8'), { filename: 'app.js' });
if (!definition) throw new Error('Vue application contract not captured');

const app = { ...Object.assign({},...definition.mixins.map(m=>m.data()),...definition.mixins.map(m=>m.methods)),...definition.data(), ...definition.methods };
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

  let pendingSignal;
  const pendingFetch = async (_, options) => {
    pendingSignal=options.signal;
    return new Promise((resolve,reject)=>{
      const abort=()=>reject(new DOMException('Cancelled','AbortError'));
      if(options.signal.aborted)abort();else options.signal.addEventListener('abort',abort,{once:true});
    });
  };
  global.fetch=pendingFetch;
  const caller=new AbortController();
  const cancelled=app.api('/fixture',{signal:caller.signal});caller.abort();
  await assert.rejects(cancelled,{name:'AbortError'});
  assert.equal(pendingSignal.aborted,true);
  assert.equal(app.connectionState,'connected');assert.equal(app.status,'正在生成');
  await assert.rejects(app.api('/fixture',{signal:caller.signal}),{name:'AbortError'});

  Object.assign(app,{rememberDialogFocus(){},focusDialog(){},restoreDialogFocus(){},attachmentName:p=>p});
  const metadata=app.openLocalFile('old.docx');const metadataSignal=pendingSignal;
  app.closeDocumentPreview();await metadata;
  assert.equal(metadataSignal.aborted,true);assert.equal(app.documentPreview.open,false);
  assert.equal(app.documentPreview.loading,false);assert.equal(app.documentPreview.error,'');

  const old=app.openLocalFile('old.docx');const replacedSignal=pendingSignal;
  global.fetch=async()=>ok({kind:'docx',html:'new document'});
  await app.openLocalFile('new.docx');await old;
  assert.equal(replacedSignal.aborted,true);assert.equal(app.documentPreview.html,'new document');

  // Abort after headers, while the PDF body is still downloading.
  let bodySignal;
  global.fetch=async(p,options)=>p.includes('/document?')?ok({kind:'pdf'}):{
    ok:true,async blob(){bodySignal=options.signal;return pendingFetch(p,options);}
  };
  const pdf=app.openLocalFile('test.pdf');
  for(let i=0;i<10&&!bodySignal;i++)await Promise.resolve();
  assert.ok(bodySignal);app.closeDocumentPreview();await pdf;
  assert.equal(bodySignal.aborted,true);assert.equal(app.documentPreview.url,'');

  global.fetch=pendingFetch;
  const unmount=app.openLocalFile('unmount.xlsx');const unmountSignal=pendingSignal;
  window.WorkbenchDocumentUi.beforeUnmount.call(app);await unmount;
  assert.equal(unmountSignal.aborted,true);assert.equal(app.documentRequestController,null);
  assert.equal(app.connectionState,'connected');assert.equal(app.status,'正在生成');

  process.stdout.write(JSON.stringify({
    uiConnectionState: 'PASS',
    staleStatusCleared: true,
    deferredActivityFailure: true,
    callerOptionsPreserved: true,
    cancellationCases: ['caller','already-aborted','metadata-close','replacement','pdf-body-close','unmount']
  }, null, 2));
}

main().catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });

const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const assert=require('node:assert/strict');
let definition;
global.Vue={createApp(value){definition=value;return{config:{},mount(){}}},nextTick(fn){if(fn)fn();return Promise.resolve()}};
global.window={addEventListener(){},removeEventListener(){},getSelection(){return null}};
global.document={documentElement:{dataset:{},style:{setProperty(){}}},querySelector(){return null},createElement(){return{}}};
global.localStorage={getItem(){return null},setItem(){},removeItem(){}};
for(const name of ['ui-store','provider-catalog','document-ui','workflow-ui'])vm.runInThisContext(fs.readFileSync(path.join(__dirname,'../static/modules',name+'.js'),'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(__dirname,'../static/app.js'),'utf8'));

(async()=>{
  const app={...definition.data(),...definition.methods};
  const handoff='下面是此前对话。请把它作为上下文，但不要复述标签；重新独立回答最后一条用户要求。';
  app.workspace='D:\\work\\Claude';app.workspaceRoot=app.workspace;
  app.sessions=[
    {id:'visible',claudeSessionId:'branch-current',title:'俄罗斯方块',workspace:app.workspace,started:true},
    {id:'branch-current',claudeSessionId:'branch-current',title:handoff,workspace:app.workspace,started:true,transcript:true},
    {id:'branch-old',claudeSessionId:'branch-old',title:handoff,workspace:app.workspace,started:true,transcript:true}
  ];
  app.api=async()=>[
    {id:'branch-current',title:handoff,workspace:app.workspace,updatedAt:'2026-09-24T01:00:00Z'},
    {id:'branch-old',title:handoff,workspace:app.workspace,updatedAt:'2026-09-24T00:00:00Z'},
    {id:'legitimate',title:'正常任务',workspace:app.workspace,updatedAt:'2026-09-24T02:00:00Z'}
  ];
  let saved=0;app.saveSessions=async()=>{saved++};
  await app.syncTranscripts();
  assert.deepEqual(app.sessions.map(item=>item.id).sort(),['legitimate','visible']);
  assert.equal(app.sessions.find(item=>item.id==='visible').title,'俄罗斯方块');
  assert.equal(saved,1);
  app.api=async()=>[{id:'branch-old',title:handoff},{id:'legitimate',title:'正常任务'}];
  await app.searchTranscripts();
  assert.deepEqual(app.transcriptResults.map(item=>item.id),['legitimate']);
  console.log(JSON.stringify({sessionTranscriptAlias:'PASS',branchTranscriptHidden:true,visibleTitlePreserved:true,legacyDuplicateRemoved:true}));
})().catch(error=>{console.error(error);process.exitCode=1});

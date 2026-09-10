const readline = require('node:readline');
const frames = value => process.stdout.write(JSON.stringify(value) + '\n');
let initialized = false, turns = 0, session;
const notify = (method, params) => frames({jsonrpc:'2.0',method,params});
readline.createInterface({input:process.stdin}).on('line', line => {
  const req=JSON.parse(line), reply=result=>frames({jsonrpc:'2.0',id:req.id,result});
  if(req.method==='initialize') {
    if(req.params.model==='fixture-oversize') { process.stdout.write('x'.repeat(8*1024*1024+1));return; }
    if(req.params.model==='fixture-flood') { for(let i=0;i<5000;i++)frames({method:'fixture',params:{i}});return; }
    if(req.params.model==='fixture-byte-flood') { for(let i=0;i<24;i++)frames({method:'fixture',params:{text:'中'.repeat(1024*1024)}});return; }
    if(req.params.model==='fixture-malformed') { process.stdout.write('{invalid json}\n');return; }
    if(req.params.model==='fixture-eof') { process.exit(0);return; }
    if(process.env.DSH_TELEMETRY_DISABLED!=='1'||process.env.DSH_PERMISSION_MODE!=='read-only') throw new Error('Unsafe launch environment');
    initialized=true; reply({serverInfo:{name:'deepseek-harness-sdk-runtime',version:'fixture'}}); return;
  }
  if(req.method==='shutdown'){reply({});process.exitCode=0;process.stdin.destroy();return;}
  if(req.method!=='session/prompt'||!initialized) throw new Error('Unexpected request');
  if(!req.params.contentBlocks.some(x=>x.text?.includes('中文'))) throw new Error('UTF-8 input was corrupted');
  if(session&&session!==req.params.sessionId) throw new Error('Session identity lost');
  session=req.params.sessionId; const id=`msg-${++turns}`;
  const event=(type,data)=>notify('session.event',{sessionId:session,event:{type,seq:turns,time:Date.now(),data}});
  // Deliberately notify before acknowledgement to exercise receipt buffering.
  event('agent/inbox/spliced',{inserted:[{id}]}); reply({messageId:id});
  notify('session.status',{sessionId:session,status:'running'});
  event('step/start',{turn:turns,step:1});
  event('tool/call',{callId:'tool-'+turns,name:'read_file',arguments:'{"path":"中文.txt"}'});
  event('tool/result',{message:{content:[{type:'tool-result',toolCallId:'tool-'+turns,isError:false,content:[{type:'text',text:'文件内容'}]}]}});
  event('assistant/chunk',{chunk:{type:'text-delta',index:0,text:'DSHarness 中文回复'}});
  event('assistant/message',{message:{content:[{type:'text',text:'DSHarness 中文回复'}]},usage:{inputTokens:3,outputTokens:4}});
  event('turn/end',{reason:{kind:'completed'}});
  notify('session.status',{sessionId:session,status:'idle'});
});

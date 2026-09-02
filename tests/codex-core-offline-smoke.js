// Genuine Codex CLI, isolated user configuration and a loopback-only Responses fixture.
const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),http=require('node:http'),{spawn}=require('node:child_process');
const [executable,codex]=process.argv.slice(2);
if(!executable||!codex)throw new Error('Usage: node codex-core-offline-smoke.js <workbench.exe> <codex.exe>');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'codex-core-中文 ')),home=path.join(root,'codex-home');fs.mkdirSync(home);
let child,calls=0,stdout='',stderr='',toolNames=[],readVerified=false,policyBlocked=false,toolOutputs=[];
const filePath=path.join(root,'中文 文件.txt'),fileEvidence='Codex 本地文件读取证据 fixture-714';fs.writeFileSync(filePath,fileEvidence,'utf8');
const server=http.createServer((req,res)=>{
  let body='';req.setEncoding('utf8');req.on('data',chunk=>body+=chunk);req.on('end',()=>{
    if(req.method!=='POST'||!req.url.endsWith('/responses')){res.writeHead(404);res.end('{}');return;}
    if(req.headers.authorization!=='Bearer fixture-offline-no-paid-key'){res.writeHead(401);res.end('{}');return;}
    const input=JSON.parse(body);if(!JSON.stringify(input.input).includes('中文')){res.writeHead(400);res.end('{}');return;}calls++;
    toolNames=(input.tools||[]).map(x=>x.name||x.type);
    if(calls===2){toolOutputs=input.input.filter(x=>x.type==='function_call_output');readVerified=toolOutputs.some(x=>JSON.stringify(x.output).includes(fileEvidence));policyBlocked=toolOutputs.some(x=>JSON.stringify(x.output).includes('blocked by policy'));}
    const item={id:'msg-'+calls,type:'message',role:'assistant',status:'completed',content:[{type:'output_text',text:policyBlocked?'Codex 核心权限拒绝已保留。':'Codex 核心中文链路正常。',annotations:[]}]};
    const response={id:'resp-'+calls,object:'response',created_at:1,status:'completed',model:input.model,output:[item],usage:{input_tokens:11,output_tokens:7,total_tokens:18,input_tokens_details:{cached_tokens:0},output_tokens_details:{reasoning_tokens:0}}};
    res.writeHead(200,{'content-type':'text/event-stream; charset=utf-8'});
    let seq=0;const event=(type,data)=>res.write(`event: ${type}\ndata: ${JSON.stringify({type,sequence_number:seq++,...data})}\n\n`);
    if(calls===1){
      const call={id:'fc-fixture',call_id:'call-fixture',type:'function_call',name:'exec_command',arguments:JSON.stringify({cmd:`Get-Content -LiteralPath '${filePath}'`,shell:'powershell',max_output_tokens:1000})};
      event('response.created',{response:{...response,status:'in_progress',output:[]}});
      event('response.output_item.added',{output_index:0,item:{...call,arguments:''}});
      event('response.function_call_arguments.delta',{item_id:call.id,output_index:0,delta:call.arguments});
      event('response.function_call_arguments.done',{item_id:call.id,output_index:0,arguments:call.arguments});
      event('response.output_item.done',{output_index:0,item:call});event('response.completed',{response:{...response,output:[call]}});res.end();return;
    }
    event('response.created',{response:{...response,status:'in_progress',output:[]}});
    event('response.output_item.added',{output_index:0,item:{...item,status:'in_progress',content:[]}});
    event('response.content_part.added',{item_id:item.id,output_index:0,content_index:0,part:{type:'output_text',text:'',annotations:[]}});
    event('response.output_text.delta',{item_id:item.id,output_index:0,content_index:0,delta:item.content[0].text});
    event('response.output_text.done',{item_id:item.id,output_index:0,content_index:0,text:item.content[0].text});
    event('response.content_part.done',{item_id:item.id,output_index:0,content_index:0,part:item.content[0]});
    event('response.output_item.done',{output_index:0,item});event('response.completed',{response});res.end();
  });
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  fs.writeFileSync(path.join(home,'config.toml'),`model = "fixture-offline"\nmodel_provider = "fixture"\n[model_providers.fixture]\nname = "Offline fixture"\nbase_url = "http://127.0.0.1:${server.address().port}/v1"\nenv_key = "CODEX_FIXTURE_KEY"\nwire_api = "responses"\nrequest_max_retries = 0\nstream_max_retries = 0\nstream_idle_timeout_ms = 10000\n`);
  const config=path.join(root,'bridge.json');fs.writeFileSync(config,JSON.stringify({harness:'codex',executable:path.resolve(codex),workspace:root,model:'fixture-offline',permissionMode:'readonly',sessionId:'fixture-codex-core',statePath:path.join(root,'state.json'),addDirs:[]}));
  const env={...process.env,CODEX_HOME:home,CODEX_FIXTURE_KEY:'fixture-offline-no-paid-key',OPENAI_API_KEY:''};delete env.CODEX_THREAD_ID;
  child=spawn(path.resolve(executable),['--agent-worker-bridge',config],{windowsHide:true,cwd:root,env});
  child.stdout.setEncoding('utf8');child.stderr.setEncoding('utf8');child.stdout.on('data',chunk=>stdout+=chunk);child.stderr.on('data',chunk=>stderr=(stderr+chunk).slice(-16000));
  const prompt=JSON.stringify({type:'user',message:{role:'user',content:[{type:'text',text:'中文测试，请读取 '+filePath+' 然后简短答复。'}]}})+'\n';child.stdin.end(prompt+prompt);
  const timer=setTimeout(()=>child.kill(),60000);const code=await new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',resolve);});clearTimeout(timer);
  const events=stdout.trim().split(/\r?\n/).filter(Boolean).map(x=>JSON.parse(x)),results=events.filter(x=>x.type==='result');
  if(code!==0||calls!==3||(!readVerified&&!policyBlocked)||results.length!==2||results.some(x=>x.is_error||!x.result.includes('Codex 核心')))throw new Error(`Genuine Codex failed: code=${code} calls=${calls} readVerified=${readVerified} results=${JSON.stringify(results)} tools=${JSON.stringify(toolOutputs).slice(0,4000)}\n${stderr}`);
  if(results[0].usage.input_tokens!==22||results[1].usage.input_tokens!==11)throw new Error('Codex cumulative usage was not converted to per-turn values');
  console.log(JSON.stringify({genuineCodexCore:'PASS',turns:2,utf8:true,resume:true,perTurnUsage:true,fileRead:readVerified,corePolicyDenied:policyBlocked,paidApiCalls:0,toolNames}));
})().catch(error=>{console.error(error.message);process.exitCode=1;}).finally(()=>{server.close();if(child&&!child.killed)child.kill();fs.rmSync(root,{recursive:true,force:true});});

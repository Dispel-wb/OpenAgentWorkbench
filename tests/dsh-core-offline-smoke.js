// Runs the genuine installed DSHarness core against a local model fixture. No paid API.
const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),http=require('node:http'),{spawn}=require('node:child_process');
const [executable,entry]=process.argv.slice(2);
if(!executable||!entry)throw new Error('Usage: node dsh-core-offline-smoke.js <workbench.exe> <dsh/lib/bin.js>');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'dsh-core-中文 '));
let calls=0,child,stdout='',stderr='',toolNames=[],readVerified=false;
const filePath=path.join(root,'中文 文件.txt'),fileEvidence='本地文件读取证据 fixture-713';
fs.writeFileSync(filePath,fileEvidence,'utf8');
const server=http.createServer((req,res)=>{
  let body='';req.setEncoding('utf8');req.on('data',chunk=>body+=chunk);req.on('end',()=>{
    if(req.method!=='POST'||!req.url.endsWith('/chat/completions')){res.writeHead(404);res.end('fixture route missing');return;}
    const request=JSON.parse(body);
    toolNames=(request.tools||[]).map(x=>x.function?.name).filter(Boolean);
    if(!JSON.stringify(request.messages).includes('中文')){res.writeHead(400);res.end('UTF-8 input missing');return;}
    calls++;
    res.writeHead(200,{'content-type':'text/event-stream; charset=utf-8'});
    const base={id:'fixture-'+calls,object:'chat.completion.chunk',created:1,model:request.model};
    if(calls===1){
      res.write('data: '+JSON.stringify({...base,choices:[{index:0,delta:{role:'assistant',tool_calls:[{index:0,id:'fixture-read',type:'function',function:{name:'read',arguments:JSON.stringify({file_path:filePath})}}]},finish_reason:null}]})+'\n\n');
      res.write('data: '+JSON.stringify({...base,choices:[{index:0,delta:{},finish_reason:'tool_calls'}],usage:{prompt_tokens:11,completion_tokens:7,total_tokens:18}})+'\n\n');
      res.end('data: [DONE]\n\n');return;
    }
    if(calls===2)readVerified=request.messages.some(x=>x.role==='tool'&&JSON.stringify(x.content).includes(fileEvidence));
    res.write('data: '+JSON.stringify({...base,choices:[{index:0,delta:{role:'assistant',content:'DSHarness 核心中文链路正常。'},finish_reason:null}]})+'\n\n');
    res.write('data: '+JSON.stringify({...base,choices:[{index:0,delta:{},finish_reason:'stop'}],usage:{prompt_tokens:11,completion_tokens:7,total_tokens:18}})+'\n\n');
    res.end('data: [DONE]\n\n');
  });
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const patch=path.join(root,'privacy.patch.json'),config=path.join(root,'bridge.json');
  fs.writeFileSync(patch,JSON.stringify(['session-telemetry-otel','session-log-deepseek','plugin-package-inventory-deepseek'].map(id=>({id,disabled:true}))));
  fs.writeFileSync(config,JSON.stringify({harness:'dsh',executable:process.execPath,entry:path.resolve(entry),profile:'sdk',patchPath:patch,home:path.join(root,'home'),workspace:root,model:'deepseek-v4-flash',permissionMode:'readonly',sessionId:'fixture-real-dsh-session',maxTurns:10}));
  child=spawn(path.resolve(executable),['--agent-worker-bridge',config],{windowsHide:true,cwd:root,env:{...process.env,DEEPSEEK_API_KEY:'fixture-offline-no-paid-key',DEEPSEEK_BASE_URL:`http://127.0.0.1:${server.address().port}`,DSH_TELEMETRY_DISABLED:'1'}});
  child.stdout.setEncoding('utf8');child.stderr.setEncoding('utf8');
  child.stdout.on('data',chunk=>stdout+=chunk);child.stderr.on('data',chunk=>stderr=(stderr+chunk).slice(-24000));
  const prompt=JSON.stringify({type:'user',message:{role:'user',content:[{type:'text',text:'中文测试，请读取 '+filePath+'，然后简短答复。'}]}})+'\n';
  child.stdin.end(prompt+prompt);
  const timer=setTimeout(()=>child.kill(),60000);
  const code=await new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',resolve);});clearTimeout(timer);
  const events=stdout.trim().split(/\r?\n/).filter(Boolean).map(line=>JSON.parse(line));
  const results=events.filter(x=>x.type==='result');
  if(code!==0||results.length!==2||results.some(x=>x.is_error)||calls!==3||!readVerified)throw new Error(`Genuine DSH core failed: code=${code}, calls=${calls}, readVerified=${readVerified}, results=${JSON.stringify(results)}\n${stderr}`);
  if(results.some((x,i)=>x.result!=='DSHarness 核心中文链路正常。'||x.usage.input_tokens!==(i===0?22:11)||x.usage.output_tokens!==(i===0?14:7)))throw new Error('Genuine core output/usage mismatch: '+JSON.stringify(results));
  console.log(JSON.stringify({genuineDshCore:'PASS',calls,turns:results.length,utf8:true,usage:true,fileRead:readVerified,paidApiCalls:0,toolNames}));
})().catch(error=>{console.error(error.message);process.exitCode=1;}).finally(()=>{server.close();if(child&&!child.killed)child.kill();fs.rmSync(root,{recursive:true,force:true});});

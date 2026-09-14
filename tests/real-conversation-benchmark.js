// Opt-in, local-only. Never include private conversation text in reports/screenshots.
const fs=require('fs'),crypto=require('crypto'),path=require('path'),{chromium}=require('playwright');
const source=process.env.WORKBENCH_BENCHMARK_CONVERSATION;
if(!source)throw new Error('Set WORKBENCH_BENCHMARK_CONVERSATION to a local JSON/JSONL chat export.');
function load(){
  if(fs.statSync(source).size>25*1024*1024)throw new Error('Export exceeds the 25 MB benchmark limit');
  const bytes=fs.readFileSync(source),raw=bytes.toString('utf8').replace(/^\uFEFF/,'');
  const rows=path.extname(source).toLowerCase()==='.jsonl'?raw.split(/\r?\n/).filter(Boolean).map(l=>JSON.parse(l)):JSON.parse(raw);
  const array=Array.isArray(rows)?rows:rows.messages;
  if(!Array.isArray(array))throw new Error('Expected messages array or Claude transcript JSONL');
  const messages=array.map((entry,index)=>{
    const m=entry.message||entry,blocks=m.content;
    let text=m.text||(typeof blocks==='string'?blocks:Array.isArray(blocks)?blocks.filter(b=>b.type==='text').map(b=>b.text||'').join('\n'):'');
    text=text.replace(/\bsk-[a-zA-Z0-9_-]{12,}\b/g,'[REDACTED_KEY]').replace(/Bearer\s+[A-Za-z0-9._-]{12,}/gi,'Bearer [REDACTED]');
    return {id:'real-'+index,role:m.role||entry.type,text,time:new Date().toISOString(),workflow:[],streaming:false};
  }).filter(m=>['user','assistant'].includes(m.role)&&m.text.trim());
  const characters=messages.reduce((n,m)=>n+m.text.length,0);
  if(characters<100000)throw new Error('Real export contains fewer than 100,000 text characters; cannot mark this gate passed.');
  return {messages,characters,sha256:crypto.createHash('sha256').update(bytes).digest('hex')};
}
async function main(){
  const data=load(),base=process.env.CLAUDE_UI_BASE_URL,workspace=process.env.CLAUDE_UI_WORKSPACE;
  const browser=await chromium.launch({headless:true,executablePath:process.env.CLAUDE_UI_BROWSER_EXE});
  try{
    const context=await browser.newContext({viewport:{width:1280,height:850},extraHTTPHeaders:{'X-Desktop-Secret':process.env.CLAUDE_UI_SECRET,'X-Workbench-Protocol':'2'}});
    const api=context.request,id='private-real-benchmark',now=new Date().toISOString();
    await api.post(base+'/api/settings',{data:{workspace,theme:'dark',skin:'codex'}});
    await api.post(base+'/api/sessions',{data:[{id,claudeSessionId:id,title:'私有长会话验收',workspace,createdAt:now,updatedAt:now,queue:[]}]});
    await api.post(base+'/api/sessions/'+id+'/messages',{data:data.messages});
    const page=await context.newPage();let errors=0;page.on('pageerror',()=>errors++);
    const start=Date.now();await page.goto(base);await page.locator('.message-body').last().waitFor();
    const loadMs=Date.now()-start,cdp=await context.newCDPSession(page);await cdp.send('Performance.enable');
    const frames=await page.evaluate(()=>new Promise(resolve=>{
      let started,last;const durations=[],container=document.querySelector('.conversation');
      function frame(now){if(started===undefined){started=now;last=now;}else{durations.push(now-last);last=now;}
        container.scrollTop=(Math.sin((now-started)/300)+1)/2*(container.scrollHeight-container.clientHeight);
        if(now-started<3000)requestAnimationFrame(frame);else{durations.sort((a,b)=>a-b);resolve({fps:durations.length*1000/(now-started),p95FrameMs:durations[Math.floor(durations.length*.95)]});}}
      requestAnimationFrame(frame);
    }));
    await page.locator('.message-body').first().focus();
    await page.evaluate(()=>{const body=document.querySelector('.message-body'),walker=document.createTreeWalker(body,NodeFilter.SHOW_TEXT);let node;while((node=walker.nextNode())&&node.length<3){}const range=document.createRange();range.setStart(node,0);range.setEnd(node,3);getSelection().removeAllRanges();getSelection().addRange(range);});
    const selection=await page.evaluate(()=>getSelection().toString());
    await page.keyboard.press('Shift+F10');await page.getByRole('menu',{name:'所选内容操作'}).waitFor();await page.keyboard.press('Escape');
    const selectionPreserved=await page.evaluate(text=>getSelection().toString()===text,selection);
    const raw=await cdp.send('Performance.getMetrics'),metrics=Object.fromEntries(raw.metrics.map(m=>[m.name,m.value]));
    const report={dataset:'real-private-local',sourceSha256:data.sha256,characters:data.characters,messages:data.messages.length,environment:'headless Chrome',loadMs,...frames,jsHeapBytes:metrics.JSHeapUsedSize,domNodes:metrics.Nodes,selectionPreserved,errors};
    report.passed=loadMs<10000&&frames.fps>=20&&metrics.JSHeapUsedSize<256*1024*1024&&selectionPreserved&&errors===0;
    fs.writeFileSync(path.join(process.env.CLAUDE_UI_OUTPUT_DIR,'real-conversation-benchmark.json'),JSON.stringify(report,null,2));
    console.log(JSON.stringify(report));if(!report.passed)process.exitCode=1;
  }finally{await browser.close();}
}
main().catch(()=>{console.error('Real conversation benchmark failed. Private content is intentionally omitted from diagnostics.');process.exitCode=1;});

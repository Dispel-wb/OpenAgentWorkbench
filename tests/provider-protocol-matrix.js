// Protocol-shape fixtures, not live-provider certification. No paid requests.
const http=require('http'),assert=require('assert/strict');
const port=Number(process.argv[2]),secret=process.argv[3],base='http://127.0.0.1:'+port;
const frame=(value,eol='\n')=>'data: '+JSON.stringify(value)+eol+eol;
const delta=(content,finish=null)=>({choices:[{index:0,delta:{content},finish_reason:finish}]});
const cases=[
  {id:'deepseek-reasoning',source:'https://api-docs.deepseek.com/api/create-chat-completion/',wire:frame({choices:[{delta:{reasoning_content:'fixture-private-reasoning'}}]})+frame(delta('中文正常','stop'))+'data: [DONE]\n\n',text:'中文正常'},
  {id:'qwen-usage-tail',source:'https://help.aliyun.com/en/model-studio/stream',wire:frame(delta('尾包正常','stop'),'\r\n')+frame({choices:[],usage:{prompt_tokens:71,completion_tokens:9}},'\r\n')+'data: [DONE]\r\n\r\n',text:'尾包正常',input:71,output:9},
  {id:'minimax-openai-content',source:'https://platform.minimax.io/docs/guides/text-chat',wire:frame(delta('中文分片'))+frame(delta('正常','length'))+'data: [DONE]\n\n',text:'中文分片正常',stop:'max_tokens'},
  {id:'typed-content-array',wire:frame(delta([{type:'text',text:'数组'},{type:'text',text:'正常'}],'stop'))+'data: [DONE]\n\n',text:'数组正常'},
  {id:'multiline-sse',wire:': heartbeat\r\nevent: completion\r\ndata: {"choices": [\r\ndata: {"delta":{"content":"多行正常"},"finish_reason":"stop"}]}\r\n\r\ndata: [DONE]\r\n\r\n',text:'多行正常'},
  {id:'parallel-tools',wire:frame({choices:[{delta:{tool_calls:[{index:0,id:'c1',function:{name:'Read',arguments:'{"path":'}},{index:1,id:'c2',function:{name:'Glob',arguments:'{"pattern":'}}]}}]})+frame({choices:[{delta:{tool_calls:[{index:1,function:{arguments:'"*.txt"}'}},{index:0,function:{arguments:'"中文.txt"}'}}]},finish_reason:'tool_calls'}]})+'data: [DONE]\n\n',tools:true},
  {id:'invalid-event',wire:'data: {broken-json}\n\n',error:true},
  {id:'oversized-line',wire:'data: '+ 'x'.repeat(2*1024*1024+1)+'\n\n',error:true},
  {id:'filtered-output',wire:frame(delta('部分内容','content_filter'))+'data: [DONE]\n\n',error:true}
];
async function main(){
  const stub=http.createServer((request,response)=>{
    let body='';request.setEncoding('utf8');request.on('data',chunk=>body+=chunk);request.on('end',()=>{
      const payload=JSON.parse(body),item=cases.find(c=>c.id===payload.model);
      if(!item){response.writeHead(400);response.end('{}');return;}
      response.writeHead(200,{'Content-Type':'text/event-stream'});
      // Split inside Chinese UTF-8 code points, independently of SSE event boundaries.
      const bytes=Buffer.from(item.wire);let offset=0;
      function write(){if(response.destroyed)return;if(offset>=bytes.length){response.end();return;}const size=item.id==='oversized-line'?8192:7;response.write(bytes.subarray(offset,offset+size));offset+=size;setImmediate(write);}
      write();
    });
  });
  await new Promise(resolve=>stub.listen(0,'127.0.0.1',resolve));
  try{
    const saved=await fetch(base+'/api/providers',{method:'POST',headers:{'X-Desktop-Secret':secret,'X-Workbench-Protocol':'2','Content-Type':'application/json'},body:JSON.stringify({id:'protocol-fixture',name:'Offline protocol matrix',token:'fixture-only-token',authStyle:'bearer',text:{enabled:true,protocol:'openai',baseUrl:'http://127.0.0.1:'+stub.address().port+'/v1',models:cases.map(c=>c.id)},image:{enabled:false,protocol:'openai-images',models:[]}})});
    assert(saved.ok,'Fixture registration failed');
    for(const item of cases){
      const response=await fetch(base+'/adapter/protocol-fixture/v1/messages',{method:'POST',headers:{Authorization:'Bearer fixture-only-token','Content-Type':'application/json'},body:JSON.stringify({model:item.id,stream:true,max_tokens:100,messages:[{role:'user',content:'仅离线测试'}]}),signal:AbortSignal.timeout(15000)});
      const text=await response.text(),events=text.split(/\r?\n/).filter(l=>l.startsWith('data: ')).map(l=>JSON.parse(l.slice(6)));
      assert(!text.includes('fixture-private-reasoning'),item.id+' leaked reasoning');
      if(item.error){assert(events.some(e=>e.type==='error'),item.id+' lacked terminal error');continue;}
      assert(events.some(e=>e.type==='message_stop'),item.id+' missing stop');
      if(item.text)assert.equal(events.filter(e=>e.delta?.type==='text_delta').map(e=>e.delta.text).join(''),item.text,item.id);
      if(item.input)assert.equal(events.find(e=>e.type==='message_delta').usage.input_tokens,item.input);
      if(item.output)assert.equal(events.find(e=>e.type==='message_delta').usage.output_tokens,item.output);
      if(item.stop)assert.equal(events.find(e=>e.type==='message_delta').delta.stop_reason,item.stop);
      if(item.tools){
        const starts=events.filter(e=>e.content_block?.type==='tool_use');assert.equal(starts.length,2);
        for(const start of starts){const args=events.filter(e=>e.index===start.index&&e.delta?.type==='input_json_delta').map(e=>e.delta.partial_json).join('');assert.doesNotThrow(()=>JSON.parse(args));}
      }
    }
    console.log(JSON.stringify({providerProtocolMatrix:'PASS',cases:cases.map(c=>c.id),paidRequests:0}));
  }finally{await new Promise(resolve=>stub.close(resolve));}
}
main().catch(error=>{console.error(error);process.exitCode=1;});

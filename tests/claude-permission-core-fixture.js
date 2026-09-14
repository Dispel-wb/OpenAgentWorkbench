// Deterministic provider only; the Claude executable and workbench permission server are genuine.
const http = require('node:http');
const fs = require('node:fs');
let calls = 0;
const server = http.createServer((req, res) => {
  let raw = '';
  req.setEncoding('utf8');
  req.on('data', chunk => { raw += chunk; });
  req.on('end', () => {
    try {
      if (req.headers.authorization !== 'Bearer offline-permission-fixture') throw Error('Wrong fixture credentials');
      if (++calls > 10) throw Error('Unexpected repeated model calls');
      const input = JSON.parse(raw), text = input.messages.map(m => typeof m.content === 'string' ? m.content : '').join('\n');
      if (input.tools) fs.writeFileSync(process.argv[2] + '.tools.json', JSON.stringify(input.tools));
      const inputPath = text.match(/读取文件 '([^']+)'/)?.[1];
      const outputPath = text.match(/原样写到 '([^']+)'/)?.[1];
      const results = input.messages.filter(m => m.role === 'tool');
      const marker = results.map(m => m.content).join('\n').match(/验收-[a-f0-9]{12}/)?.[0];
      const recall = text.includes('只回复上一轮文件里的完整内容');
      const dagMarker=text.match(/交接-[a-f0-9]{10}/)?.[0];
      const parent=text.includes('父任务乙就绪');
      const outside=text.match(/permission-boundary: '([^']+)'/)?.[1];
      if ((!inputPath || !outputPath) && !recall && !dagMarker && !parent && !outside) throw Error('Missing Chinese fixture paths (or unexpected auxiliary request)');
      let delta, finish = 'stop';
      const tool = (name, args, id) => ({ tool_calls: [{ index: 0, id, type: 'function', function: { name, arguments: JSON.stringify(args) } }] });
      if (outside) {
        if(!results.length){delta=tool('Edit',{file_path:outside,old_string:'',new_string:'must-not-write'},'call_denied');finish='tool_calls';}
        else{if(fs.existsSync(outside))throw Error('SECURITY: unauthorized outside write succeeded');delta={content:'安全边界拒绝正常'};}
      } else if (dagMarker || parent) { delta={content:dagMarker || '父任务乙就绪。'}; }
      else if (recall) {
        const remembered = marker || text.match(/验收-[a-f0-9]{12}/)?.[0];
        if (!remembered) throw Error('Core history lost the marker');
        delta = { content: remembered };
      } else if (!results.length) { delta = tool('Read', { file_path: inputPath }, 'call_read'); finish = 'tool_calls'; }
      else if (results.length === 1) {
        if (!marker) throw Error('Genuine Read result missing: ' + String(results[0].content).slice(0, 350));
        const hasWrite = input.tools?.some(t => t.function.name === 'Write');
        delta = hasWrite ? tool('Write', { file_path: outputPath, content: marker }, 'call_write') : tool('Edit', { file_path: outputPath, old_string: '', new_string: marker }, 'call_write'); finish = 'tool_calls';
      } else {
        if (!fs.existsSync(outputPath) || fs.readFileSync(outputPath, 'utf8').trim() !== marker) throw Error('Genuine Write not executed: ' + JSON.stringify(results).slice(-1600));
        delta = { content: text.includes('只回复上一轮文件里的完整内容') ? marker : '文件处理完成。' };
      }
      res.writeHead(200, { 'content-type': 'text/event-stream; charset=utf-8' });
      res.write(`data: ${JSON.stringify({ choices: [{ index: 0, delta, finish_reason: null }] })}\n\n`);
      res.write(`data: ${JSON.stringify({ choices: [{ index: 0, delta: {}, finish_reason: finish }], usage: { prompt_tokens: 50, completion_tokens: 15 } })}\n\n`);
      res.end('data: [DONE]\n\n');
    } catch (error) { fs.writeFileSync(process.argv[2]+'.failure.json', raw); res.writeHead(400, { 'content-type': 'application/json' }); res.end(JSON.stringify({ error: { message: error.message } })); }
  });
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(process.argv[2], String(server.address().port)));

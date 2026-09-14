const fs = require('node:fs');
const http = require('node:http');
const portFile = process.argv[2];
const server = http.createServer((req, res) => {
  let body = ''; req.setEncoding('utf8').on('data', c => body += c).on('end', () => {
    const parsed = JSON.parse(body);
    if (req.headers.authorization !== 'Bearer pi-host-local-fixture') { res.writeHead(401); return res.end(); }
    fs.writeFileSync(portFile + '.request.json', JSON.stringify(parsed));
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    if (JSON.stringify(parsed.messages.at(-1)).includes('PI_HOST_HANG')) { res.write(': keepalive\n\n'); return; }
    const chunk = (delta, finish_reason) => res.write('data: ' + JSON.stringify({ id: 'host-fixture', object: 'chat.completion.chunk', created: 1, model: 'fixture-model', choices: [{ index: 0, delta, finish_reason }] }) + '\n\n');
    chunk({ role: 'assistant', content: 'Pi 工作台端到端中文回复' }, null); chunk({}, 'stop'); res.end('data: [DONE]\n\n');
  });
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port)));

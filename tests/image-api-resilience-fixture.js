const fs = require('fs');
const http = require('http');

const portFile = process.argv[2];
const stateFile = process.argv[3];
const token = 'sk-image-fixture-12345678';
const png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlXl1sAAAAASUVORK5CYII=';
const state = { requests: 0, closed: 0, authFailures: 0 };
const save = () => fs.writeFileSync(stateFile, JSON.stringify(state));

const server = http.createServer((request, response) => {
  if (request.url !== '/v1/images/generations') {
    response.writeHead(404); response.end(); return;
  }
  let body = '';
  request.setEncoding('utf8');
  request.on('data', chunk => { body += chunk; });
  request.on('end', () => {
    const payload = JSON.parse(body || '{}');
    state.requests++;
    state.lastModel = payload.model;
    save();

    let counted = false;
    const recordClose = () => {
      if (counted || response.writableEnded) return;
      counted = true; state.closed++; save();
    };
    request.on('aborted', recordClose);
    request.on('close', () => { if (!response.writableEnded) recordClose(); });
    response.on('close', recordClose);

    if (request.headers['x-api-key'] !== token) {
      state.authFailures++; save();
      response.writeHead(401, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify({ error: { message: 'x-api-key required' } }));
      return;
    }
    if (payload.model === 'hang-image') return;
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify({ data: [{ b64_json: png }], usage: { total_tokens: 7 } }));
  });
});

server.listen(0, '127.0.0.1', () => { fs.writeFileSync(portFile, String(server.address().port)); save(); });
const close = () => server.close(() => process.exit(0));
process.on('SIGTERM', close);
process.on('SIGINT', close);

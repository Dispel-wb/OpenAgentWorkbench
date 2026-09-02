const fs = require('fs');
const http = require('http');

const portFile = process.argv[2];
const mode = process.argv[3] || 'healthy';
if (!portFile) throw new Error('port file is required');
let bootstrapRequests = 0;

function json(response, value) {
  const body = Buffer.from(JSON.stringify(value));
  response.writeHead(200, {'content-type': 'application/json', 'content-length': body.length});
  response.end(body);
}

const server = http.createServer((request, response) => {
  if (request.url.startsWith('/api/bootstrap')) {
    bootstrapRequests += 1;
    if (mode === 'permanent-failure') {
      request.socket.destroy();
      return;
    }
    const delay = mode === 'transient' && bootstrapRequests === 1 ? 6500 :
      mode === 'transient' && bootstrapRequests === 2 ? 2500 : 0;
    setTimeout(() => json(response, {persistence: {integrity: 'ok'}}), delay);
    return;
  }
  if (request.url.startsWith('/api/workbench/health')) {
    json(response, {durableJobState: true});
    return;
  }
  response.writeHead(404);
  response.end();
});

server.listen(0, '127.0.0.1', () => {
  fs.writeFileSync(portFile, String(server.address().port), 'utf8');
});

function stop() { server.close(() => process.exit(0)); }
process.on('SIGTERM', stop);
process.on('SIGINT', stop);

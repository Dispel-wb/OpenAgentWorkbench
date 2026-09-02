const fs = require('fs');
const http = require('http');

const portFile = process.argv[2];
if (!portFile) throw new Error('port file is required');
const token = 'sk-model-discovery-fixture-12345678';

const server = http.createServer((request, response) => {
  if (request.url === '/hang/models' || request.url === '/hang/v1/models') return;

  if (request.url === '/v1/models') {
    if (request.headers['x-api-key'] !== token) {
      response.writeHead(401, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify({ error: { message: `wrong auth for ${token}` } }));
      return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    response.end(JSON.stringify({ data: { models: [
      { model_id: 'nested-chat-model', capabilities: { tools: true, context_window: 65536 } },
      { id: 'nested-image-model', output_modalities: ['image'] }
    ] } }));
    return;
  }

  if (request.url === '/v1/chat/completions') {
    if (request.headers['x-api-key'] !== token) {
      response.writeHead(401, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify({ error: { message: 'x-api-key required' } }));
      return;
    }
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      const payload = JSON.parse(body || '{}');
      if (payload.stream === false) {
        response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        response.end(JSON.stringify({
          choices: [{ message: { role: 'assistant', content: null, tool_calls: [{ id: 'call_auth_probe', type: 'function', function: { name: 'workbench_capability_probe', arguments: '{"ok":true}' } }] } }],
          usage: { prompt_tokens: 5, completion_tokens: 2 }
        }));
        return;
      }
      response.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8' });
      response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: `自动鉴权正常：${payload.model}` } }] })}\n\n`);
      response.write(`data: ${JSON.stringify({ choices: [{ delta: {}, finish_reason: 'stop' }], usage: { prompt_tokens: 6, completion_tokens: 3 } })}\n\n`);
      response.end('data: [DONE]\n\n');
    });
    return;
  }

  response.writeHead(404, { 'Content-Type': 'application/json; charset=utf-8' });
  response.end(JSON.stringify({ error: { message: 'not found' } }));
});

server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port), 'utf8'));
const close = () => server.close(() => process.exit(0));
process.on('SIGTERM', close);
process.on('SIGINT', close);

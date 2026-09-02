const fs = require('fs');
const http = require('http');

const portFile = process.argv[2];
if (!portFile) throw new Error('port file is required');

const models = [
  {
    id: 'vision-tools-128k',
    context_length: 131072,
    top_provider: { max_completion_tokens: 8192 },
    architecture: { input_modalities: ['text', 'image'], output_modalities: ['text'] },
    supported_parameters: ['temperature', 'tools', 'tool_choice']
  },
  {
    id: 'text-only-32k',
    capabilities: { vision: false, tools: false, context_window: 32768, max_output_tokens: 4096 },
    input_modalities: ['text'],
    output_modalities: ['text']
  },
  {
    id: 'dual-output-model',
    output_modalities: ['text', 'image'],
    supported_parameters: ['tools']
  },
  {
    id: 'image-only-model',
    output_modalities: ['image']
  },
  { id: 'id-only-model' }
];

const server = http.createServer((request, response) => {
  if (request.url === '/v1/models' && request.headers.authorization === 'Bearer fixture-token') {
    const body = Buffer.from(JSON.stringify({ data: models }), 'utf8');
    response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': body.length });
    response.end(body);
    return;
  }
  response.writeHead(404, { 'Content-Type': 'application/json' });
  response.end('{"error":"not found"}');
});

server.listen(0, '127.0.0.1', () => {
  fs.writeFileSync(portFile, String(server.address().port), 'utf8');
});

const close = () => server.close(() => process.exit(0));
process.on('SIGTERM', close);
process.on('SIGINT', close);

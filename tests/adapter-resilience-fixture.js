const http = require('http');
const fs = require('fs');

const portFile = process.argv[2];
const stateFile = process.argv[3];
if (!portFile || !stateFile) throw new Error('Usage: node adapter-resilience-fixture.js <port-file> <state-file>');

const state = { requests: 0, closed: 0, modes: {} };
function save() { fs.writeFileSync(stateFile, JSON.stringify(state)); }

const server = http.createServer((request, response) => {
  let body = '';
  request.setEncoding('utf8');
  request.on('data', chunk => { body += chunk; });
  request.on('end', () => {
    let payload = {};
    try { payload = JSON.parse(body || '{}'); } catch {}
    const model = String(payload.model || '');
    const mode = model.includes('stream-error') ? 'stream-error' : model.includes('truncated') ? 'truncated' : model.includes('no-stream-options') ? 'no-stream-options' : model.includes('bad-body-hang') ? 'bad-body-hang' : model.includes('bad') ? 'bad' : model.includes('idle') ? 'idle' : model.includes('hang') ? 'hang' : model.includes('auto-xkey') ? 'auto-xkey' : 'capture';
    state.requests++;
    state.modes[mode] = (state.modes[mode] || 0) + 1;
    state.requestModels = state.requestModels || [];
    state.requestModels.push(model);
    if (mode === 'capture') {
      state.lastRequest = {
        model,
        bodyBytes: Buffer.byteLength(body),
        keys: Object.keys(payload).sort(),
        messageCount: Array.isArray(payload.messages) ? payload.messages.length : 0,
        messageShape: Array.isArray(payload.messages) ? payload.messages.map(message => ({
          role: message.role,
          contentType: Array.isArray(message.content) ? 'array' : typeof message.content,
          contentLength: typeof message.content === 'string' ? message.content.length : JSON.stringify(message.content || '').length
        })) : [],
        toolCount: Array.isArray(payload.tools) ? payload.tools.length : 0,
        tools: Array.isArray(payload.tools) ? payload.tools.map(tool => ({
          name: tool.function?.name,
          descriptionLength: String(tool.function?.description || '').length,
          parameters: tool.function?.parameters || {}
        })) : [],
        stream: payload.stream === true,
        hasStreamOptions: payload.stream_options != null,
        maxTokens: payload.max_tokens
      };
    }
    save();

    let countedClose = false;
    function recordClose() {
      if (countedClose || response.writableEnded) return;
      countedClose = true;
      state.closed++;
      save();
    }
    request.on('aborted', recordClose);
    request.on('close', () => { if (!response.writableEnded) recordClose(); });
    request.socket.on('close', recordClose);
    response.on('close', recordClose);

    if (mode === 'auto-xkey' && request.headers['x-api-key'] !== 'sk-fixture-secret-12345678') {
      response.writeHead(401, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify({ error: { message: 'x-api-key required' } }));
      return;
    }
    if (mode === 'no-stream-options' && payload.stream_options != null) {
      response.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify({ error: { message: 'Unsupported field: stream_options' } }));
      return;
    }

    if (mode === 'bad') {
      response.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify({ error: { message: '模型不可用；泄漏测试 sk-fixture-secret-12345678' } }));
      return;
    }
    if (mode === 'bad-body-hang') {
      response.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
      response.flushHeaders();
      return;
    }
    if (mode === 'idle') {
      response.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8' });
      response.flushHeaders();
      return;
    }
    if (mode === 'stream-error') {
      response.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8' });
      response.end(`data: ${JSON.stringify({ error: { message: 'fixture stream protocol error' } })}\n\n`);
      return;
    }
    if (mode === 'truncated') {
      response.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8' });
      response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '部分内容' } }] })}\n\n`);
      response.end();
      return;
    }
    if (mode === 'hang') return;
    response.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8' });
    response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '本地链路正常' } }] })}\n\n`);
    response.write(`data: ${JSON.stringify({ choices: [{ delta: {} }], usage: { prompt_tokens: 10, completion_tokens: 4 } })}\n\n`);
    response.end('data: [DONE]\n\n');
  });
});

server.listen(0, '127.0.0.1', () => {
  fs.writeFileSync(portFile, String(server.address().port));
  save();
});

process.on('SIGTERM', () => server.close(() => process.exit(0)));

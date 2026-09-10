const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const exe = path.resolve(process.argv[2] || 'dist/pi-candidate/OpenAgentWorkbench.exe');
const realEntry = path.resolve('runtimes/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pi-worker-中文 '));
const write = (file, data) => fs.writeFileSync(file, JSON.stringify(data), 'utf8');
const input = text => JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text }] } });
let count = 0;
function config(name, entry, mode = 'readonly', baseUrl = '', api = 'openai-completions') {
  const home = path.join(root, name); fs.mkdirSync(home, { recursive: true });
  write(path.join(home, 'models.json'), { providers: { workbench: { baseUrl, api, apiKey: '$WORKBENCH_PI_API_KEY', models: [{ id: 'fixture-model', input: ['text'], reasoning: false }] } } });
  write(path.join(home, 'settings.json'), { retry: { enabled: false }, compaction: { enabled: false }, packages: [] });
  const result = { harness: 'pi', executable: process.execPath, entry, home, workspace: home, model: 'fixture-model', permissionMode: mode, sessionId: name, statePath: path.join(home, 'state.json'), maxTurns: 8 };
  const file = path.join(home, 'bridge.json'); write(file, result); return { ...result, file };
}
function run(cfg, prompts, timeout = 20000) {
  return new Promise((resolve, reject) => {
    const child = spawn(exe, ['--agent-worker-bridge', cfg.file], { windowsHide: true, env: { ...process.env, WORKBENCH_PI_API_KEY: 'fixture-local-only', WORKBENCH_TEST_PROCESS: '1' } });
    let output = '', error = '';
    const timer = setTimeout(() => { child.kill(); reject(new Error('Bridge timeout: ' + cfg.file + '\n' + output + error)); }, timeout);
    child.stdout.setEncoding('utf8').on('data', chunk => { output += chunk; });
    child.stderr.setEncoding('utf8').on('data', chunk => { error += chunk; });
    child.on('error', reject);
    child.on('close', code => { clearTimeout(timer); try { resolve({ code, events: output.trim().split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line)), error }); } catch (e) { reject(e); } });
    child.stdin.on('error', () => {});
    child.stdin.end(prompts.map(input).join('\n') + '\n');
  });
}
function results(run) { return run.events.filter(event => event.type === 'result'); }
async function main() {
  const fake = path.join(__dirname, 'fake-pi-worker.js');
  const good = await run(config('fake-good', fake), ['hello', 'again']);
  assert.equal(good.code, 0); assert.equal(results(good).length, 2);
  for (const item of results(good)) { assert.equal(item.is_error, false); assert.equal(item.result, '中文\u2028流式'); assert.deepEqual(item.usage, { input_tokens: 11, output_tokens: 7, cache_read_input_tokens: 3, cache_creation_input_tokens: 2 }); }
  assert.equal(good.events.filter(e => e.type === 'stream_event').length, 2); count++;
  for (const [name, reason] of [['reject', 'fixture rejected'], ['malformed', '无效 JSON'], ['oversize', 'character limit'], ['no-terminal', '完整终态'], ['approval', '未自动批准'], ['readonly-tool', '禁用工具模式'], ['model-error', 'fixture model error']]) {
    const failed = await run(config('fake-' + name, fake), [name]);
    assert.notEqual(failed.code, 0, name); assert.equal(results(failed).length, 1, name);
    assert.equal(results(failed)[0].is_error, true, name); assert.ok(results(failed)[0].result.includes(reason), JSON.stringify(failed)); count++;
  }
  for (const mode of ['agent', 'edit', 'manual', 'scoped', 'invalid']) {
    const failed = await run(config('mode-' + mode, fake, mode), ['hello']);
    assert.equal(results(failed)[0].is_error, true); assert.match(results(failed)[0].result, /未启动核心/); count++;
  }
  let requests = [];
  const server = http.createServer((req, res) => {
    let body = ''; req.setEncoding('utf8').on('data', c => body += c).on('end', () => {
      try {
        if (req.url.startsWith('/anthropic/')) {
          assert.equal(new URL(req.url, 'http://localhost').pathname, '/anthropic/v1/messages');
          assert.equal(req.headers['x-api-key'], 'fixture-local-only');
          const parsed = JSON.parse(body); requests.push(parsed);
          assert.equal((parsed.tools || []).length, 0);
          res.writeHead(200, { 'content-type': 'text/event-stream' });
          const event = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
          event('message_start', { message: { id: 'msg-fixture', type: 'message', role: 'assistant', model: 'fixture-model', content: [], stop_reason: null, usage: { input_tokens: 12, output_tokens: 0 } } });
          event('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
          event('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'Anthropic 中文回复' } });
          event('content_block_stop', { index: 0 });
          event('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 6 } });
          event('message_stop', {}); res.end(); return;
        }
        assert.equal(req.headers.authorization, 'Bearer fixture-local-only');
        assert.equal(req.url, '/v1/chat/completions');
        const parsed = JSON.parse(body); requests.push(parsed);
        const user = parsed.messages.filter(m => m.role === 'user').at(-1);
        const userText = typeof user.content === 'string' ? user.content : JSON.stringify(user.content);
        if (userText.includes('REAL_ERROR')) { res.writeHead(400, { 'Content-Type': 'application/json' }); return res.end(JSON.stringify({ error: { message: 'fixture real API failure', type: 'invalid_request_error' } })); }
        res.writeHead(200, { 'Content-Type': 'text/event-stream' });
        const send = (delta, finish_reason = null) => res.write('data: ' + JSON.stringify({ id: 'chatcmpl-fixture', object: 'chat.completion.chunk', created: 1, model: 'fixture-model', choices: [{ index: 0, delta, finish_reason }] }) + '\n\n');
        send({ role: 'assistant' });
        if (userText.includes('REAL_WRITE') && parsed.messages.at(-1).role !== 'tool') {
          assert.ok(parsed.tools.some(t => t.function.name === 'write'));
          send({ tool_calls: [{ index: 0, id: 'fixture-write', type: 'function', function: { name: 'write', arguments: JSON.stringify({ path: 'pi-proof.txt', content: 'Pi 原生写入成功\n' }) } }] });
          send({}, 'tool_calls');
        } else {
          if (userText.includes('REAL_RESUME')) assert.ok(JSON.stringify(parsed.messages).includes('REMEMBER_MARKER_中文'), 'Native session did not resume');
          if (!userText.includes('REAL_WRITE')) assert.equal((parsed.tools || []).length, 0, 'Readonly exposed tools');
          send({ content: userText.includes('REAL_WRITE') ? '文件已写入' : '真实 Pi 中文流式回复' }); send({}, 'stop');
        }
        res.write('data: ' + JSON.stringify({ id: 'chatcmpl-fixture', object: 'chat.completion.chunk', choices: [], usage: { prompt_tokens: 24, completion_tokens: 8, total_tokens: 32 } }) + '\n\n');
        res.end('data: [DONE]\n\n');
      } catch (error) { res.destroy(error); console.error(error); }
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const baseUrl = `http://127.0.0.1:${server.address().port}/v1`;
  try {
    const real = config('real-readonly', realEntry, 'readonly', baseUrl);
    const first = await run(real, ['REMEMBER_MARKER_中文', 'REAL_RESUME']);
    assert.equal(first.code, 0, JSON.stringify(first)); assert.equal(results(first).length, 2); count++;
    assert.ok(first.events.some(e => e.type === 'stream_event'));
    const resumed = await run(real, ['REAL_RESUME']); assert.equal(resumed.code, 0, JSON.stringify(resumed)); count++;
    const full = config('real-full', realEntry, 'full', baseUrl);
    const tool = await run(full, ['REAL_WRITE']); assert.equal(tool.code, 0, JSON.stringify(tool));
    assert.equal(fs.readFileSync(path.join(full.workspace, 'pi-proof.txt'), 'utf8'), 'Pi 原生写入成功\n');
    assert.ok(tool.events.some(e => e.type === 'assistant' && e.message.content[0].type === 'tool_use'));
    assert.ok(tool.events.some(e => e.type === 'user' && e.message.content[0].type === 'tool_result')); count++;
    const failed = await run(config('real-error', realEntry, 'readonly', baseUrl), ['REAL_ERROR']);
    assert.notEqual(failed.code, 0); assert.equal(results(failed)[0].is_error, true); count++;
    const anthropic = await run(config('real-anthropic', realEntry, 'readonly', baseUrl.replace('/v1', '/anthropic'), 'anthropic-messages'), ['中文 Anthropic']);
    assert.equal(anthropic.code, 0, JSON.stringify(anthropic));
    assert.equal(results(anthropic)[0].result, 'Anthropic 中文回复');
    assert.equal(results(anthropic)[0].usage.input_tokens, 12); assert.equal(results(anthropic)[0].usage.output_tokens, 6); count++;
    console.log(JSON.stringify({ status: 'PASS', cases: count, realRequests: requests.length, root }));
  } finally { await new Promise(resolve => server.close(resolve)); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });

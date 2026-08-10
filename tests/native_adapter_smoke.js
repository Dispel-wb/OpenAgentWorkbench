const http = require('http');

const appPort = Number(process.argv[2]);
const secret = process.argv[3];
if (!appPort || !secret) throw new Error('Usage: node native_adapter_smoke.js <app-port> <secret>');

async function main() {
  let received;
  const stub = http.createServer((request, response) => {
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      received = JSON.parse(body || '{}');
      if (received.stream) {
        response.writeHead(200, { 'Content-Type': 'text/event-stream' });
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '流式中文' } }] })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [{ delta: {} }], usage: { prompt_tokens: 12, completion_tokens: 3 } })}\n\n`);
        response.end('data: [DONE]\n\n');
      } else {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({
          choices: [{ message: { role: 'assistant', content: '离线回复' } }],
          usage: { prompt_tokens: 8, completion_tokens: 2 }
        }));
      }
    });
  });
  await new Promise(resolve => stub.listen(0, '127.0.0.1', resolve));
  const stubPort = stub.address().port;

  try {
    const base = `http://127.0.0.1:${appPort}`;
    const root = await (await fetch(`${base}/`)).text();
    if (/desktop-secret|__DESKTOP_SECRET__/.test(root)) throw new Error('desktop secret leaked into HTML');
    await fetch(`${base}/api/providers`, {
      method: 'POST',
      headers: { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2', 'Content-Type': 'application/json' },
      body: JSON.stringify({
        id: 'adapter-stub', name: 'Adapter 离线测试', token: 'stub-token', authStyle: 'bearer',
        text: { enabled: true, protocol: 'openai', baseUrl: `http://127.0.0.1:${stubPort}/v1`, models: ['stub-model'] },
        image: { enabled: false, protocol: 'openai-images', baseUrl: '', models: [] }
      })
    });

    const payload = {
      model: 'stub-model', max_tokens: 100, stream: false,
      system: [{ type: 'text', text: '默认中文语境' }],
      messages: [{ role: 'user', content: [{ type: 'text', text: '你好' }] }]
    };
    const nonstreamResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const nonstream = await nonstreamResponse.json();
    const upstreamHadSystem = received.messages?.[0]?.role === 'system' && received.messages[0].content === '默认中文语境';

    payload.stream = true;
    const streamResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const streamText = await streamResponse.text();
    const result = {
      nonstreamStatus: nonstreamResponse.status,
      nonstreamText: nonstream.content?.[0]?.text,
      inputTokens: nonstream.usage?.input_tokens,
      outputTokens: nonstream.usage?.output_tokens,
      upstreamHadSystem,
      streamStatus: streamResponse.status,
      streamHasMessageStart: streamText.includes('event: message_start'),
      streamHasChineseDelta: streamText.includes('流式中文'),
      streamHasMessageStop: streamText.includes('event: message_stop')
    };
    console.log(JSON.stringify(result, null, 2));
    if (result.nonstreamStatus !== 200 || result.nonstreamText !== '离线回复' || result.inputTokens !== 8 ||
        result.outputTokens !== 2 || !result.upstreamHadSystem || result.streamStatus !== 200 ||
        !result.streamHasMessageStart || !result.streamHasChineseDelta || !result.streamHasMessageStop) process.exitCode = 1;
  } finally {
    await new Promise(resolve => stub.close(resolve));
  }
}

main().catch(error => { console.error(error); process.exit(1); });

const http = require('http');

const appPort = Number(process.argv[2]);
const secret = process.argv[3];
if (!appPort || !secret) throw new Error('Usage: node native_adapter_smoke.js <app-port> <secret>');

async function main() {
  let received, receivedPath;
  const stub = http.createServer((request, response) => {
    receivedPath = request.url;
    let body = '';
    request.setEncoding('utf8');
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      received = JSON.parse(body || '{}');
      if (received.model === 'missing-model') {
        response.writeHead(400, { 'Content-Type': 'application/json; charset=utf-8' });
        response.end(JSON.stringify({ error: { message: "Model 'missing-model' is not available" } }));
        return;
      }
      if (received.model === 'empty-nonstream') {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ choices: [], usage: {} }));
        return;
      }
      if (received.stream) {
        response.writeHead(200, { 'Content-Type': 'text/event-stream' });
        if (received.model === 'stub-reasoning-only') {
          response.write(`data: ${JSON.stringify({ choices: [{ delta: { reasoning_content: '只有内部推理' } }] })}\n\n`);
          response.write(`data: ${JSON.stringify({ choices: [{ delta: {}, finish_reason: 'stop' }] })}\n\n`);
          response.end('data: [DONE]\n\n');
          return;
        }
        if (received.model === 'stub-tool-model') {
          response.write(`data: ${JSON.stringify({ choices: [{ delta: { tool_calls: [{ index: 0, id: 'call_fixture', function: { name: 'Read', arguments: '' } }] } }] })}\n\n`);
          response.write(`data: ${JSON.stringify({ choices: [], usage: { prompt_tokens: 14, completion_tokens: 5 } })}\n\n`);
          response.write(`data: ${JSON.stringify({ choices: [{ delta: { tool_calls: [{ index: 0, function: { arguments: '{\"file_path\":\"C:/fixture.txt\"}' } }] }, finish_reason: 'tool_calls' }] })}\n\n`);
          response.end('data: [DONE]\n\n');
          return;
        }
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { reasoning_content: '内部推理不应显示' } }] })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [{ delta: { content: '流式中文' } }] })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [], usage: { prompt_tokens: 12, completion_tokens: 3 } })}\n\n`);
        response.write(`data: ${JSON.stringify({ choices: [{ delta: {}, finish_reason: 'stop' }] })}\n\n`);
        response.end('data: [DONE]\n\n');
      } else {
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({
          choices: [{ message: received.model === 'stub-probe-model' ? { role: 'assistant', content: null, tool_calls: [{ id: 'call_probe', type: 'function', function: { name: 'workbench_capability_probe', arguments: '{"ok":true}' } }] } : { role: 'assistant', content: '离线回复' } }],
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
        text: { enabled: true, protocol: 'openai', baseUrl: `http://127.0.0.1:${stubPort}/v4`, models: ['stub-model', 'stub-tool-model', 'stub-vision-model', 'stub-probe-model', 'stub-reasoning-only', 'missing-model', 'empty-nonstream'] },
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
    const versionedBasePathCorrect = receivedPath === '/v4/chat/completions';

    const visionResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify({
        ...payload, model: 'stub-vision-model',
        messages: [{ role: 'user', content: [
          { type: 'text', text: '看图' },
          { type: 'image', source: { type: 'base64', media_type: 'image/png', data: 'aGVsbG8=' } }
        ] }]
      })
    });
    await visionResponse.json();
    const upstreamVisionContent = received.messages?.find(message => message.role === 'user')?.content;
    const upstreamHadVision = Array.isArray(upstreamVisionContent) && upstreamVisionContent.some(item =>
      item?.type === 'image_url' && item.image_url?.url === 'data:image/png;base64,aGVsbG8=' && item.image_url?.detail === 'auto');

    payload.stream = true;
    const streamResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const streamText = await streamResponse.text();

    payload.model = 'stub-tool-model';
    payload.tools = [{ name: 'Read', description: 'Read a file', input_schema: { type: 'object', properties: { file_path: { type: 'string' } }, required: ['file_path'] } }];
    const toolResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const toolText = await toolResponse.text();

    payload.model = 'stub-reasoning-only'; delete payload.tools;
    const reasoningOnlyResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const reasoningOnlyText = await reasoningOnlyResponse.text();

    payload.model = 'empty-nonstream'; payload.stream = false; delete payload.tools;
    const emptyResponse = await fetch(`${base}/adapter/adapter-stub/v1/messages`, {
      method: 'POST', headers: { Authorization: 'Bearer stub-token', 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const emptyBody = await emptyResponse.json();
    const ipcHeaders = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2', 'Content-Type': 'application/json' };
    const probeResponse = await fetch(`${base}/api/providers/validate-model`, { method: 'POST', headers: ipcHeaders, body: JSON.stringify({ providerId: 'adapter-stub', model: 'stub-probe-model' }) });
    const probeBody = await probeResponse.json();
    const missingResponse = await fetch(`${base}/api/providers/validate-model`, { method: 'POST', headers: ipcHeaders, body: JSON.stringify({ providerId: 'adapter-stub', model: 'missing-model' }) });
    const missingBody = await missingResponse.json();
    const result = {
      nonstreamStatus: nonstreamResponse.status,
      nonstreamText: nonstream.content?.[0]?.text,
      inputTokens: nonstream.usage?.input_tokens,
      outputTokens: nonstream.usage?.output_tokens,
      upstreamHadSystem,
      versionedBasePathCorrect,
      visionStatus: visionResponse.status,
      upstreamHadVision,
      streamStatus: streamResponse.status,
      streamHasMessageStart: streamText.includes('event: message_start'),
      streamHasChineseDelta: streamText.includes('流式中文'),
      streamReasoningSuppressed: !streamText.includes('内部推理不应显示'),
      streamHasMessageStop: streamText.includes('event: message_stop'),
      usageOnlyChunkSafe: !streamText.includes('event: error'),
      toolStatus: toolResponse.status,
      toolHasBlock: toolText.includes('"type":"tool_use"') && toolText.includes('call_fixture'),
      toolStopReason: toolText.includes('"stop_reason":"tool_use"'),
      reasoningOnlyRejected: reasoningOnlyText.includes('event: error') && reasoningOnlyText.includes('没有可显示的最终内容') && !reasoningOnlyText.includes('只有内部推理'),
      emptyStatus: emptyResponse.status,
      emptyErrorVisible: emptyBody?.error?.message?.includes('choices[0].message') === true,
      modelProbeStatus: probeResponse.status,
      modelProbeTools: probeBody.toolsVerified === true && probeBody.health?.state === 'healthy' && probeBody.provider?.capabilities?.models?.['stub-probe-model']?.tools === true,
      missingModelStatus: missingResponse.status,
      missingModelPersistent: missingBody.code === 'model_unavailable' && missingBody.health?.state === 'unavailable' && missingBody.health?.available === false
    };
    console.log(JSON.stringify(result, null, 2));
    if (result.nonstreamStatus !== 200 || result.nonstreamText !== '离线回复' || result.inputTokens !== 8 ||
        result.outputTokens !== 2 || !result.upstreamHadSystem || !result.versionedBasePathCorrect || result.visionStatus !== 200 || !result.upstreamHadVision || result.streamStatus !== 200 ||
        !result.streamHasMessageStart || !result.streamHasChineseDelta || !result.streamReasoningSuppressed || !result.streamHasMessageStop || !result.usageOnlyChunkSafe ||
        result.toolStatus !== 200 || !result.toolHasBlock || !result.toolStopReason || !result.reasoningOnlyRejected || result.emptyStatus !== 502 || !result.emptyErrorVisible ||
        result.modelProbeStatus !== 200 || !result.modelProbeTools || result.missingModelStatus !== 409 || !result.missingModelPersistent) process.exitCode = 1;
  } finally {
    await new Promise(resolve => stub.close(resolve));
  }
}

main().catch(error => { console.error(error); process.exit(1); });

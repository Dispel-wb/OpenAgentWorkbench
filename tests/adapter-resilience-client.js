const appPort = Number(process.argv[2]);
const secret = process.argv[3];
const stateFile = process.argv[4];
if (!appPort || !secret || !stateFile) throw new Error('Usage: node adapter-resilience-client.js <port> <secret> <state-file>');

const base = `http://127.0.0.1:${appPort}`;
const http = require('http');
const token = 'sk-fixture-secret-12345678';
const providerId = 'adapter-resilience';
const headers = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
const payload = model => ({ model, max_tokens: 20, stream: true, messages: [{ role: 'user', content: '测试' }] });

async function stream(model, runId, abortAfterHeaders = false) {
  const controller = new AbortController();
  const started = Date.now();
  const response = await fetch(`${base}/adapter/${providerId}/${runId}/v1/messages`, {
    method: 'POST', headers, body: JSON.stringify(payload(model)), signal: controller.signal
  });
  if (abortAfterHeaders) {
    try { if (response.body) await response.body.cancel(); }
    finally { controller.abort(); }
    return { status: response.status, elapsedMs: Date.now() - started, text: '' };
  }
  const text = await response.text();
  return { status: response.status, elapsedMs: Date.now() - started, text };
}

async function hardDisconnect(model, runId) {
  const body = JSON.stringify(payload(model));
  const target = new URL(`${base}/adapter/${providerId}/${runId}/v1/messages`);
  await new Promise((resolve, reject) => {
    const request = http.request({
      hostname: target.hostname,
      port: target.port,
      path: target.pathname,
      method: 'POST',
      headers: { ...headers, 'Content-Length': Buffer.byteLength(body) }
    }, response => {
      const socket = response.socket;
      response.destroy();
      request.destroy();
      if (socket) socket.destroy();
      resolve();
    });
    request.setTimeout(4000, () => request.destroy(new Error('Timed out waiting for Adapter response headers')));
    request.once('error', error => {
      if (error.code === 'ECONNRESET') resolve();
      else reject(error);
    });
    request.end(body);
  });
}

async function waitForClose(before) {
  const expires = Date.now() + 6000;
  while (Date.now() < expires) {
    let state;
    try { state = JSON.parse(require('fs').readFileSync(stateFile, 'utf8')); }
    catch { await new Promise(resolve => setTimeout(resolve, 25)); continue; }
    if (state.closed > before) return state.closed;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Downstream disconnect did not cancel the upstream request');
}

async function waitForRequest(before, runId = '') {
  const expires = Date.now() + 12000;
  while (Date.now() < expires) {
    let state;
    try { state = JSON.parse(require('fs').readFileSync(stateFile, 'utf8')); }
    catch { await new Promise(resolve => setTimeout(resolve, 25)); continue; }
    if (state.requests > before) return state.requests;
    if (runId) {
      const poll = await api(`/api/chat/poll/${runId}`);
      const runState = String(poll.status?.terminalState || poll.status?.state || '');
      if (['failed', 'cancelled', 'completed'].includes(runState)) {
        throw new Error(`Run ended before reaching upstream: ${runState} ${String(poll.error || poll.status?.message || '')}`);
      }
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Fake Claude Worker did not reach the upstream fixture');
}

async function waitForDisconnectOutcome(before) {
  const started = Date.now();
  const expires = started + 6000;
  let sawUpstreamRequest = false;
  while (Date.now() < expires) {
    let state;
    try { state = JSON.parse(require('fs').readFileSync(stateFile, 'utf8')); }
    catch { await new Promise(resolve => setTimeout(resolve, 25)); continue; }
    if (state.requests > before.requests) sawUpstreamRequest = true;
    if (state.closed > before.closed) return { safe: true, mode: 'cancelled-upstream' };
    if (!sawUpstreamRequest && Date.now() - started >= 4500) return { safe: true, mode: 'blocked-before-upstream' };
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Downstream disconnect left an upstream request alive');
}

async function api(path, method = 'GET', body) {
  const response = await fetch(base + path, {
    method,
    headers: { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const value = await response.json();
  if (!response.ok) throw new Error(`${method} ${path} failed: ${response.status} ${JSON.stringify(value)}`);
  return value;
}

async function waitForRun(runId, expected, timeoutMs = 5000) {
  const expires = Date.now() + timeoutMs;
  let cursor = 0;
  while (Date.now() < expires) {
    const poll = await api(`/api/chat/poll/${runId}?after=${cursor}`);
    cursor = Number(poll.nextSeq || cursor);
    const state = String(poll.status?.terminalState || poll.status?.state || '');
    if (state === expected) return poll;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Run ${runId} did not become ${expected}`);
}

async function main() {
  const head = await fetch(`${base}/adapter/${providerId}/api/hello`, { method: 'HEAD' });
  const headText = await head.text();

  const oversized = await fetch(`${base}/api/settings`, {
    method: 'POST',
    headers: { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2', 'Content-Type': 'application/json' },
    body: JSON.stringify({ padding: 'x'.repeat(12000) })
  });
  const oversizedBody = await oversized.json();
  if (oversized.status !== 413 || !String(oversizedBody.error || '').includes('安全上限')) {
    throw new Error(`Oversized local API body was not rejected: ${oversized.status} ${JSON.stringify(oversizedBody)}`);
  }

  const bad = await stream('bad-model', crypto.randomUUID());
  if (bad.status !== 200 || !bad.text.includes('event: error') || !bad.text.includes('invalid_request_error')) {
    throw new Error(`400 was not returned as an Anthropic stream error: ${bad.status} ${bad.text}`);
  }
  if (bad.text.includes(token) || bad.elapsedMs > 2500) throw new Error('400 response was slow or leaked the API key');

  const autoXkey = await stream('auto-xkey-model', crypto.randomUUID());
  if (!autoXkey.text.includes('本地链路正常') || autoXkey.elapsedMs > 2500) {
    throw new Error(`Automatic Chat authentication did not retry x-api-key: ${autoXkey.elapsedMs}ms ${autoXkey.text}`);
  }

  const streamOptionsFallback = await stream('no-stream-options-model', crypto.randomUUID());
  if (!streamOptionsFallback.text.includes('本地链路正常') || streamOptionsFallback.elapsedMs > 2500) {
    throw new Error(`Optional stream_options compatibility retry failed: ${streamOptionsFallback.elapsedMs}ms ${streamOptionsFallback.text}`);
  }

  const badBodyHang = await stream('bad-body-hang-model', crypto.randomUUID());
  if (!badBodyHang.text.includes('timeout_error') || badBodyHang.elapsedMs > 6000) {
    throw new Error(`Hanging upstream error body was not cancelled: ${badBodyHang.elapsedMs}ms ${badBodyHang.text}`);
  }

  const hang = await stream('hang-model', crypto.randomUUID());
  if (!hang.text.includes('timeout_error') || hang.elapsedMs > 6000) throw new Error(`Header timeout failed: ${hang.elapsedMs}ms`);

  const idle = await stream('idle-model', crypto.randomUUID());
  if (!idle.text.includes('timeout_error') || idle.elapsedMs > 6000) throw new Error(`Idle timeout failed: ${idle.elapsedMs}ms`);

  const truncated = await stream('truncated-model', crypto.randomUUID());
  if (!truncated.text.includes('event: error') || !truncated.text.includes('完成标记前意外中断')) {
    throw new Error(`Truncated upstream stream was accepted as success: ${truncated.text}`);
  }

  const before = JSON.parse(require('fs').readFileSync(stateFile, 'utf8'));
  await hardDisconnect('hang-model', crypto.randomUUID());
  const disconnect = await waitForDisconnectOutcome(before);

  const beforeRun = JSON.parse(require('fs').readFileSync(stateFile, 'utf8'));
  const sessionId = crypto.randomUUID();
  const run = await api('/api/chat/start', 'POST', {
    workspace: process.cwd(), prompt: 'adapter-run-cancel', sessionId, claudeSessionId: sessionId, resume: false,
    requestId: `adapter-run-cancel-${crypto.randomUUID()}`, providerId, model: 'hang-model', effort: 'low',
    permissionMode: 'readonly', maxTurns: 10, attachments: [], allowedDirs: [], allowedTools: [], disallowedTools: []
  });
  await waitForRequest(beforeRun.requests, run.jobId);
  const stopStarted = Date.now();
  await api(`/api/chat/stop/${run.jobId}`, 'POST', {});
  const runClosed = await waitForClose(beforeRun.closed);
  const runCancelMs = Date.now() - stopStarted;
  if (runCancelMs > 3000) throw new Error(`Run cancellation took ${runCancelMs}ms`);

  const errorSessionId = crypto.randomUUID();
  const errorStarted = Date.now();
  const errorRun = await api('/api/chat/start', 'POST', {
    workspace: process.cwd(), prompt: 'adapter-run-error', sessionId: errorSessionId, claudeSessionId: errorSessionId, resume: false,
    requestId: `adapter-run-error-${crypto.randomUUID()}`, providerId, model: 'bad-model', effort: 'low',
    permissionMode: 'readonly', maxTurns: 10, attachments: [], allowedDirs: [], allowedTools: [], disallowedTools: []
  });
  const failed = await waitForRun(errorRun.jobId, 'failed');
  const errorRunMs = Date.now() - errorStarted;
  const visibleError = String(failed.error || failed.status?.message || '');
  if (!visibleError.includes('上游 HTTP 400') || visibleError.includes(token) || errorRunMs > 3000) {
    throw new Error(`Run-level Adapter error was not surfaced safely: ${errorRunMs}ms ${visibleError}`);
  }

  const streamErrorSessionId = crypto.randomUUID();
  const streamErrorStarted = Date.now();
  const streamErrorRun = await api('/api/chat/start', 'POST', {
    workspace: process.cwd(), prompt: 'adapter-run-stream-error', sessionId: streamErrorSessionId, claudeSessionId: streamErrorSessionId, resume: false,
    requestId: `adapter-run-stream-error-${crypto.randomUUID()}`, providerId, model: 'stream-error-model', effort: 'low',
    permissionMode: 'readonly', maxTurns: 10, attachments: [], allowedDirs: [], allowedTools: [], disallowedTools: []
  });
  const streamFailed = await waitForRun(streamErrorRun.jobId, 'failed');
  const streamErrorMs = Date.now() - streamErrorStarted;
  const streamVisibleError = String(streamFailed.error || streamFailed.status?.message || '');
  if (!streamVisibleError.includes('fixture stream protocol error') || streamErrorMs > 3000) {
    throw new Error(`HTTP 200 stream error was not promoted to Run failure: ${streamErrorMs}ms ${streamVisibleError}`);
  }

  console.log(JSON.stringify({
    adapterResilience: 'PASS', headStatus: head.status, headBodyBytes: Buffer.byteLength(headText), requestBodyLimitStatus: oversized.status,
    errorLatencyMs: bad.elapsedMs, autoXkeyMs: autoXkey.elapsedMs, streamOptionsFallback: true, errorBodyTimeoutMs: badBodyHang.elapsedMs,
    headerTimeoutMs: hang.elapsedMs, idleTimeoutMs: idle.elapsedMs,
    truncatedStreamRejected: truncated.text.includes('完成标记前意外中断'),
    upstreamCancelledAfterDisconnect: disconnect.safe, disconnectMode: disconnect.mode,
    upstreamCancelledAfterRunStop: runClosed > beforeRun.closed,
    runCancelMs, runErrorVisibleMs: errorRunMs, streamErrorVisibleMs: streamErrorMs,
    streamErrorPromotedToRunFailure: streamVisibleError.includes('fixture stream protocol error'),
    runErrorRedacted: !visibleError.includes(token), apiKeyRedacted: !bad.text.includes(token)
  }, null, 2));
}

main().catch(error => { console.error(error); process.exit(1); });

const port = Number(process.argv[2]);
const secret = process.argv[3];
if (!port || !secret) throw new Error('Usage: node native_offline_smoke.js <port> <secret>');

async function main() {
  const base = `http://127.0.0.1:${port}`;
  const rootResponse = await fetch(`${base}/`);
  const root = await rootResponse.text();
  if (/desktop-secret|__DESKTOP_SECRET__/.test(root)) throw new Error('desktop secret leaked into HTML');
  const api = async (path, options = {}) => {
    const response = await fetch(`${base}${path}`, {
      ...options,
      headers: {
        'X-Desktop-Secret': secret,
        'X-Workbench-Protocol': '2',
        'Content-Type': 'application/json',
        ...(options.headers || {})
      }
    });
    return { status: response.status, data: await response.json() };
  };

  const unauthorized = await fetch(`${base}/api/bootstrap`);
  const provider = await api('/api/providers', {
    method: 'POST',
    body: JSON.stringify({
      id: 'offline-dual',
      name: '本地双能力测试',
      token: 'sk-test-only',
      authStyle: 'bearer',
      text: { enabled: true, protocol: 'openai', baseUrl: 'http://127.0.0.1:9/v1', models: ['text-local'] },
      image: { enabled: true, protocol: 'openai-images', baseUrl: 'http://127.0.0.1:9/v1', models: ['image-local'] }
    })
  });
  await api('/api/sessions', {
    method: 'POST',
    body: JSON.stringify([{ id: 'utf8-session', title: '中文编码验证', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), started: false }])
  });

  const bootstrap = await api('/api/bootstrap');
  const providers = await api('/api/providers');
  const adapterResponse = await fetch(`${base}/adapter/offline-dual/v1/messages/count_tokens`, {
    method: 'POST',
    headers: { Authorization: 'Bearer sk-test-only', 'Content-Type': 'application/json' },
    body: JSON.stringify({ system: '默认中文语境', messages: [{ role: 'user', content: '验证中文' }] })
  });
  const tokenCount = await adapterResponse.json();
  const css = await (await fetch(`${base}/static/styles.css`)).text();
  const evidence = await api('/api/chat/evidence/offline-fixture');

  const result = {
    unauthorizedStatus: unauthorized.status,
    providerStatus: provider.status,
    dualText: provider.data.text.enabled,
    dualImage: provider.data.image.enabled,
    publicHasToken: providers.data[0].hasToken,
    publicLeaksToken: Object.prototype.hasOwnProperty.call(providers.data[0], 'tokenEncrypted'),
    savedChineseTitle: bootstrap.data.sessions[0].title,
    adapterStatus: adapterResponse.status,
    adapterInputTokens: tokenCount.input_tokens,
    cssEmbedded: css.includes('--claude'),
    evidenceStatus: evidence.status,
    evidenceRunId: evidence.data.runId,
    evidenceSummaryShape: Number(evidence.data.summary?.contextSources) === 0 && Array.isArray(evidence.data.artifacts) && Array.isArray(evidence.data.tools),
    backend: bootstrap.data.backend,
    trayMode: bootstrap.data.trayMode
  };
  console.log(JSON.stringify(result, null, 2));
  if (result.unauthorizedStatus !== 403 || result.providerStatus !== 200 || !result.dualText || !result.dualImage ||
      !result.publicHasToken || result.publicLeaksToken || result.savedChineseTitle !== '中文编码验证' ||
      result.adapterStatus !== 200 || !result.cssEmbedded || result.evidenceStatus !== 200 || result.evidenceRunId !== 'offline-fixture' || !result.evidenceSummaryShape || result.backend !== 'C#/.NET native host' || !result.trayMode) {
    process.exitCode = 1;
  }
}

main().catch(error => { console.error(error); process.exit(1); });

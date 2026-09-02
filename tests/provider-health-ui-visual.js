const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const baseUrl = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const outputDir = process.env.CLAUDE_UI_OUTPUT_DIR;
const browserExecutable = process.env.CLAUDE_UI_BROWSER_EXE;
if (!baseUrl || !secret || !outputDir || !browserExecutable) throw new Error('Provider UI visual environment is incomplete');
fs.mkdirSync(outputDir, { recursive: true });

const headers = {
  'X-Desktop-Secret': secret,
  'X-Workbench-Protocol': '2'
};
const provider = {
  id: 'provider-ui-fixture',
  name: 'Provider 健康演示',
  token: 'stub-provider-ui-token',
  authStyle: 'bearer',
  capabilities: {
    schemaVersion: 2,
    evidencePolicy: 'fixture',
    models: {
      'fixture-vision-32k': { vision: true, tools: true, contextWindow: 32768, maxOutputTokens: 4096, evidence: 'model-endpoint-metadata', sourceUrl: 'http://127.0.0.1:9/models' },
      'fixture-id-only': { vision: null, tools: null, contextWindow: null, maxOutputTokens: null, evidence: 'model-endpoint-id-only' }
    }
  },
  text: { enabled: true, protocol: 'anthropic', baseUrl: 'http://127.0.0.1:9', models: ['fixture-vision-32k'] },
  image: { enabled: false, protocol: 'openai-images', baseUrl: '', models: [] }
};
const settings = {
  providerId: provider.id,
  model: 'fixture-vision-32k',
  effort: 'high',
  permissionMode: 'readonly',
  allowedTools: 'Read,Glob,Grep,Skill',
  disallowedTools: '',
  workspace: process.env.CLAUDE_UI_WORKSPACE,
  theme: 'dark',
  skin: 'open'
};

async function main() {
const browser = await chromium.launch({ headless: true, executablePath: browserExecutable });
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, extraHTTPHeaders: headers });
  const request = context.request;
  let response = await request.post(baseUrl + '/api/providers', { data: provider });
  if (!response.ok()) throw new Error('Fixture Provider save failed: ' + response.status());
  response = await request.post(baseUrl + '/api/settings', { data: settings });
  if (!response.ok()) throw new Error('Fixture settings save failed: ' + response.status());
  await request.post(baseUrl + '/api/providers/discover', { data: { providerId: provider.id, token: provider.token, baseUrl: provider.text.baseUrl, authStyle: provider.authStyle } });

  const page = await context.newPage();
  const openProviderSettings = async () => {
    await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    await page.locator('.sidebar-actions button').filter({ hasText: '设置' }).click();
    await page.locator('.provider-modal').waitFor({ state: 'visible' });
    await page.locator('.provider-health-panel').waitFor({ state: 'visible' });
  };
  const assertLayout = async label => {
    const result = await page.evaluate(() => ({
      viewport: window.innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      panelWidth: document.querySelector('.provider-health-panel')?.getBoundingClientRect().width || 0,
      panelVisible: !!document.querySelector('.provider-health-panel'),
      dotVisible: !!document.querySelector('.provider-health-panel .provider-health-dot'),
      matrixWidth: document.querySelector('.model-capability-matrix')?.getBoundingClientRect().width || 0,
      matrixOverflow: (() => { const value=document.querySelector('.model-capability-matrix');return value?value.scrollWidth-value.clientWidth:0; })(),
      tokenValue: document.querySelector('input[type="password"]')?.value || ''
    }));
    if (!result.panelVisible || !result.dotVisible || result.panelWidth < 240) throw new Error(label + ': health evidence is not visible');
    if (result.matrixWidth < 240 || result.matrixOverflow > 1) throw new Error(label + ': capability matrix is missing or overflowing');
    if (result.documentWidth > result.viewport + 1) throw new Error(label + ': horizontal overflow');
    if (result.tokenValue) throw new Error(label + ': saved token was exposed in the UI');
    return result;
  };

  await openProviderSettings();
  const dark = await assertLayout('dark');
  await page.screenshot({ path: path.join(outputDir, 'provider-health-ui-640-dark.png'), fullPage: false });
  await page.locator('.model-capability-matrix').screenshot({ path: path.join(outputDir, 'model-capability-ui-640-dark.png') });

  await page.setViewportSize({ width: 920, height: 760 });
  const narrow = await assertLayout('narrow');
  await page.screenshot({ path: path.join(outputDir, 'provider-health-ui-640-narrow.png'), fullPage: false });
  await page.locator('.model-capability-matrix').screenshot({ path: path.join(outputDir, 'model-capability-ui-640-narrow.png') });

  settings.theme = 'light';
  response = await request.post(baseUrl + '/api/settings', { data: settings });
  if (!response.ok()) throw new Error('Light settings save failed: ' + response.status());
  await page.setViewportSize({ width: 1440, height: 900 });
  await openProviderSettings();
  const light = await assertLayout('light');
  const theme = await page.evaluate(() => document.documentElement.dataset.theme);
  if (theme !== 'light') throw new Error('Light theme was not applied');
  await page.screenshot({ path: path.join(outputDir, 'provider-health-ui-640-light.png'), fullPage: false });
  await page.locator('.model-capability-matrix').screenshot({ path: path.join(outputDir, 'model-capability-ui-640-light.png') });

  process.stdout.write(JSON.stringify({ providerHealthUi: 'PASS', dark, narrow, light, tokenLeaked: false }, null, 2));
} finally {
  await browser.close();
}
}

main().catch(error => {
  process.stderr.write((error && error.stack) || String(error));
  process.exitCode = 1;
});

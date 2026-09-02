const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const baseUrl = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const outputDir = process.env.CLAUDE_UI_OUTPUT_DIR;
const workspace = process.env.CLAUDE_UI_WORKSPACE;
const browserExecutable = process.env.CLAUDE_UI_BROWSER_EXE;
if (!baseUrl || !secret || !outputDir || !workspace || !browserExecutable) throw new Error('MCP visual environment is incomplete');
fs.mkdirSync(outputDir, { recursive: true });

const headers = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };
const settings = {
  providerId: '', model: '', effort: 'high', permissionMode: 'readonly',
  allowedTools: 'Read,Glob,Grep,Skill', disallowedTools: '', workspace,
  theme: 'dark', skin: 'open'
};

async function openExtensions(page) {
  await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
  await page.locator('.app-shell').waitFor({ state: 'attached' });
  await page.locator('.workbench-tools button').filter({ hasText: '扩展' }).evaluate(button => button.click());
  await page.locator('.mcp-runtime-summary').waitFor({ state: 'visible' });
  await page.locator('.extension-actions button').filter({ hasText: '刷新' }).evaluate(button => button.click());
  await page.waitForTimeout(250);
}

async function assertRuntime(page, label) {
  const result = await page.evaluate(() => {
    const card = document.querySelector('.mcp-runtime-summary');
    return {
      documentWidth: document.documentElement.scrollWidth,
      viewport: window.innerWidth,
      cardWidth: card?.getBoundingClientRect().width || 0,
      text: card?.textContent || '',
      body: document.body.textContent || ''
    };
  });
  if (result.cardWidth < 220 || !result.text.includes('fixture-runtime') || !result.text.includes('stdio')) {
    throw new Error(label + ': MCP runtime summary is incomplete: ' + JSON.stringify(result));
  }
  if (result.documentWidth > result.viewport + 1) throw new Error(label + ': horizontal overflow');
  if (result.body.includes('fixture-env-secret')) throw new Error(label + ': MCP env value leaked into the UI');
  return result;
}

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: browserExecutable });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, extraHTTPHeaders: headers });
    const request = context.request;
    let response = await request.post(baseUrl + '/api/workbench/extensions/trust', { data: { workspace, type: 'control', name: '.mcp.json', trusted: true } });
    if (!response.ok()) throw new Error('MCP fixture trust failed: ' + response.status());
    const extensionProbe = await request.get(baseUrl + '/api/workbench/extensions?workspace=' + encodeURIComponent(workspace));
    const extensionProbeData = await extensionProbe.json();
    if (!extensionProbe.ok() || extensionProbeData?.mcpRuntime?.serverCount !== 1) throw new Error('MCP fixture endpoint was incomplete: ' + JSON.stringify(extensionProbeData));
    response = await request.post(baseUrl + '/api/settings', { data: settings });
    if (!response.ok()) throw new Error('Dark settings save failed: ' + response.status());

    const page = await context.newPage();
    await openExtensions(page);
    const dark = await assertRuntime(page, 'dark');
    await page.screenshot({ path: path.join(outputDir, 'mcp-runtime-ui-640-dark.png'), fullPage: false });

    await page.setViewportSize({ width: 920, height: 760 });
    const narrow = await assertRuntime(page, 'narrow');
    await page.screenshot({ path: path.join(outputDir, 'mcp-runtime-ui-640-narrow.png'), fullPage: false });

    settings.theme = 'light';
    response = await request.post(baseUrl + '/api/settings', { data: settings });
    if (!response.ok()) throw new Error('Light settings save failed: ' + response.status());
    await page.setViewportSize({ width: 1440, height: 900 });
    await openExtensions(page);
    const light = await assertRuntime(page, 'light');
    const theme = await page.evaluate(() => document.documentElement.dataset.theme);
    if (theme !== 'light') throw new Error('Light theme was not applied');
    await page.screenshot({ path: path.join(outputDir, 'mcp-runtime-ui-640-light.png'), fullPage: false });

    process.stdout.write(JSON.stringify({ dark, narrow, light, secretVisible: false }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });

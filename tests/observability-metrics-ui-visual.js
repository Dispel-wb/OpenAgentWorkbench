const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const baseUrl = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const outputDir = process.env.CLAUDE_UI_OUTPUT_DIR;
const browserExecutable = process.env.CLAUDE_UI_BROWSER_EXE;
const workspace = process.env.CLAUDE_UI_WORKSPACE;
if (!baseUrl || !secret || !outputDir || !browserExecutable || !workspace) throw new Error('Metrics UI visual environment is incomplete');
fs.mkdirSync(outputDir, { recursive: true });
const headers = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };

async function main() {
  const browser = await chromium.launch({ headless: true, executablePath: browserExecutable });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, extraHTTPHeaders: headers });
    const request = context.request;
    const settings = { workspace, theme: 'dark', skin: 'open', providerId: '', model: '', effort: 'low', permissionMode: 'readonly', allowedTools: 'Read,Glob,Grep,Skill', disallowedTools: '' };
    await request.post(baseUrl + '/api/settings', { data: settings });
    await request.get(baseUrl + '/api/workbench/health?workspace=' + encodeURIComponent(workspace));
    const page = await context.newPage();
    const openHealth = async () => {
      await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
      await page.locator('.sidebar-actions button').filter({ hasText: '开发工作台' }).click();
      await page.locator('.inspector header nav button').filter({ hasText: '诊断' }).click();
      await page.locator('.metrics-overview').waitFor({ state: 'visible' });
    };
    const assertLayout = async label => {
      const result = await page.evaluate(() => {
        const panel = document.querySelector('.health-pane');
        const metrics = document.querySelector('.metrics-overview');
        const cards = [...document.querySelectorAll('.metrics-overview>div')];
        return {
          viewport: window.innerWidth,
          documentWidth: document.documentElement.scrollWidth,
          panelWidth: panel?.getBoundingClientRect().width || 0,
          metricsWidth: metrics?.getBoundingClientRect().width || 0,
          metricsOverflow: metrics ? metrics.scrollWidth - metrics.clientWidth : 999,
          cards: cards.length,
          labels: cards.map(card => card.querySelector('span')?.textContent.trim() || ''),
          values: cards.map(card => card.querySelector('b')?.textContent.trim() || '')
        };
      });
      if (result.cards !== 4 || result.panelWidth < 260 || result.metricsWidth < 240) throw new Error(label + ': metrics summary is incomplete');
      if (result.metricsOverflow > 1 || result.documentWidth > result.viewport + 1) throw new Error(label + ': horizontal overflow');
      if (result.values.some(value => !value)) throw new Error(label + ': metric value is missing');
      if (result.labels.join('|') !== '本次 HTTP|本次 Run|本次请求 P95|本次 Host 异常') throw new Error(label + ': current-session metric hierarchy is missing');
      return result;
    };
    await openHealth();
    const dark = await assertLayout('dark');
    await page.screenshot({ path: path.join(outputDir, 'observability-ui-dark.png') });
    await page.setViewportSize({ width: 920, height: 760 });
    const narrow = await assertLayout('narrow');
    await page.screenshot({ path: path.join(outputDir, 'observability-ui-narrow.png') });
    settings.theme = 'light';
    await request.post(baseUrl + '/api/settings', { data: settings });
    await page.setViewportSize({ width: 1440, height: 900 });
    await openHealth();
    const theme = await page.evaluate(() => document.documentElement.dataset.theme);
    if (theme !== 'light') throw new Error('Light theme was not applied');
    const light = await assertLayout('light');
    await page.screenshot({ path: path.join(outputDir, 'observability-ui-light.png') });
    process.stdout.write(JSON.stringify({ observabilityUi: 'PASS', dark, narrow, light }, null, 2));
  } finally { await browser.close(); }
}
main().catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });

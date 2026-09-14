const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const { CLAUDE_UI_BASE_URL: baseUrl, CLAUDE_UI_SECRET: secret, CLAUDE_UI_WORKSPACE: workspace, CLAUDE_UI_BROWSER_EXE: executablePath } = process.env;
if (!baseUrl || !secret || !workspace || !executablePath) throw new Error('Attachment picker UI environment is incomplete');
const headers = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };

async function main() {
  const fixture = path.join(workspace, '附件选择验证.txt');
  fs.writeFileSync(fixture, 'attachment picker fixture', 'utf8');
  const browser = await chromium.launch({ headless: true, executablePath });
  try {
    const context = await browser.newContext({ viewport: { width: 900, height: 720 }, extraHTTPHeaders: headers });
    const request = context.request;
    const saveSettings = workerHarness => request.post(baseUrl + '/api/settings', { data: {
      workspace, workerHarness, providerId: '', model: '', effort: 'high', permissionMode: 'readonly', theme: 'dark', skin: 'fusion'
    }});

    if (!(await saveSettings('claude')).ok()) throw new Error('Unable to seed Claude attachment state');
    const page = await context.newPage();
    await page.route('**/api/files/select', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ files: [fixture] }) }));
    await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    const picker = page.locator('.composer-hints > button').first();
    await picker.waitFor({ state: 'visible' });
    if (await picker.isDisabled()) throw new Error('Claude attachment picker is disabled without an imported Provider');
    if (!(await picker.getAttribute('title') || '').includes('添加文件')) throw new Error('Claude attachment picker title is misleading');
    await picker.click();
    await page.locator('.attachment-card').filter({ hasText: '附件选择验证.txt' }).waitFor({ state: 'visible' });

    if (!(await saveSettings('pi')).ok()) throw new Error('Unable to seed Pi attachment state');
    const piPage = await context.newPage();
    await piPage.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    const piPicker = piPage.locator('.composer-hints > button').first();
    await piPicker.waitFor({ state: 'visible' });
    if (!(await piPicker.isDisabled())) throw new Error('Pi attachment picker must remain disabled');
    if (!(await piPicker.getAttribute('title') || '').includes('Pi')) throw new Error('Pi attachment limitation is not explained');
    await piPage.close();
    await page.close();
    process.stdout.write(JSON.stringify({ attachmentPickerUi: 'PASS', claudeWithoutProvider: true, selectedFileVisible: true, piFailClosed: true }, null, 2));
  } finally { await browser.close(); }
}

main().catch(error => { process.stderr.write(error?.stack || String(error)); process.exitCode = 1; });

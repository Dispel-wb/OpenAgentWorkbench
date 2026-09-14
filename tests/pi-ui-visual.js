const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const { chromium } = require('playwright');
async function main() {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CLAUDE_UI_BROWSER_EXE });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, extraHTTPHeaders: { 'X-Desktop-Secret': process.env.CLAUDE_UI_SECRET, 'X-Workbench-Protocol': '2' } });
    const page = await context.newPage(); const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(process.env.CLAUDE_UI_BASE_URL);
    await page.locator('button[title="API 设置与归档聊天"]').click();
    await page.locator('.settings-nav button').filter({ hasText: '工作区' }).click();
    const core = page.locator('.claude-runtime-card select').first();
    await core.selectOption('pi');
    await page.getByPlaceholder('@earendil-works/pi-coding-agent/dist/bundle/cli.js 的绝对路径').waitFor();
    const diagnostic = await context.request.get(process.env.CLAUDE_UI_BASE_URL + '/api/workbench/runtime/agents?probe=1');
    const info = await diagnostic.json(); assert.equal(info.selected, 'pi'); assert.equal(info.available, true); assert.equal(info.pi.version, '0.85.1');
    await page.reload();
    await page.locator('button[title="API 设置与归档聊天"]').click();
    await page.locator('.settings-nav button').filter({ hasText: '工作区' }).click();
    assert.equal(await core.inputValue(), 'pi');
    const card = page.locator('.claude-runtime-card').first();
    await page.locator('.claude-runtime-card.ready').first().waitFor();
    assert.ok((await card.innerText()).includes('无工作区沙箱'));
    fs.mkdirSync(process.env.CLAUDE_UI_OUTPUT_DIR, { recursive: true });
    for (const size of [{ width: 1280, height: 900 }, { width: 826, height: 760 }]) {
      await page.setViewportSize(size);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
      await page.screenshot({ path: path.join(process.env.CLAUDE_UI_OUTPUT_DIR, `pi-settings-${size.width}.png`) });
    }
    assert.deepEqual(errors, []); console.log(JSON.stringify({ PiUI: 'PASS', widths: [1280, 826], selectionPersisted: true, diagnostics: true }));
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });

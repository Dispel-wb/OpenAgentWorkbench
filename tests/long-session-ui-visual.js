const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const baseUrl = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const outputDir = process.env.CLAUDE_UI_OUTPUT_DIR;
const browserExecutable = process.env.CLAUDE_UI_BROWSER_EXE;
const workspace = process.env.CLAUDE_UI_WORKSPACE;
if (!baseUrl || !secret || !outputDir || !browserExecutable || !workspace) throw new Error('Long-session UI environment is incomplete');
fs.mkdirSync(outputDir, { recursive: true });
const headers = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };

async function main() {
  const browser = await chromium.launch({ headless: true, executablePath: browserExecutable });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, extraHTTPHeaders: headers });
    const request = context.request;
    const sessionId = 'long-session-fixture';
    const now = new Date().toISOString();
    const messages = [];
    for (let index = 0; index < 420; index++) {
      const role = index % 2 ? 'assistant' : 'user';
      const text = index === 419
        ? '# 超长回复\n\n' + ('这是用于验证渐进 Markdown 排版、选区稳定和 DOM 上限的中文内容。\n'.repeat(4200)) + '\nD:\\work\\Claude\\fixture.pdf'
        : `${role === 'user' ? '用户' : 'Agent'}消息 ${index}\n\n- 长会话窗口测试\n- 保持滚动锚点\n\n${'中文上下文 '.repeat(80)}`;
      messages.push({ id: `message-${String(index).padStart(4, '0')}`, role, text, time: now, workflow: [], streaming: false });
    }
    await request.post(baseUrl + '/api/settings', { data: { workspace, theme: 'dark', skin: 'codex', providerId: '', model: '', effort: 'low', permissionMode: 'readonly', allowedTools: 'Read,Glob,Grep,Skill', disallowedTools: '' } });
    await request.post(baseUrl + '/api/sessions', { data: [{ id: sessionId, claudeSessionId: sessionId, title: '十万字长会话验证', createdAt: now, updatedAt: now, workspace, started: false, allowedDirs: [], queue: [] }] });
    await request.post(`${baseUrl}/api/sessions/${sessionId}/messages`, { data: messages });
    const storedSessions = await (await request.get(baseUrl + '/api/bootstrap')).json();
    const storedMessages = await (await request.get(`${baseUrl}/api/sessions/${sessionId}/messages`)).json();
    if (!storedSessions.sessions?.some(session => session.id === sessionId) || storedMessages.length !== messages.length) throw new Error(`Fixture persistence failed: ${storedMessages.length}/${messages.length}`);

    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.stack || error.message));
    const started = Date.now();
    await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    const last = page.locator('[data-message-id="message-0419"]');
    try { await last.waitFor({ state: 'visible', timeout: 12000 }); }
    catch (error) {
      const diagnostics = await page.evaluate(() => ({ status: document.querySelector('.statusline')?.innerText || '', messages: document.querySelectorAll('.message').length, body: document.body.innerText.slice(0, 1600), lastError: document.querySelector('#app')?.dataset.lastError || '' })).catch(() => ({}));
      await page.screenshot({ path: path.join(outputDir, 'long-session-ui-failed.png') }).catch(() => {});
      throw new Error(`Last message did not render. pageErrors=${JSON.stringify(pageErrors)} diagnostics=${JSON.stringify(diagnostics)} cause=${error.message}`);
    }
    const initialRenderMs = Date.now() - started;
    const initial = await page.evaluate(() => {
      const articles = [...document.querySelectorAll('.conversation>.message')];
      const last = document.querySelector('[data-message-id="message-0419"]');
      return {
        renderedMessages: articles.length,
        firstId: articles[0]?.dataset.messageId || '',
        lastId: articles.at(-1)?.dataset.messageId || '',
        longBodyCharacters: last?.querySelector('.message-body')?.innerText.length || 0,
        omittedControls: document.querySelectorAll('.long-message-control').length,
        documentOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        heapBytes: performance.memory?.usedJSHeapSize || 0
      };
    });
    if (initial.renderedMessages > 80 || initial.renderedMessages < 60) throw new Error(`Rendered message bound failed: ${initial.renderedMessages}`);
    if (initial.firstId !== 'message-0340' || initial.lastId !== 'message-0419') throw new Error('Initial message window is not the newest bounded slice');
    if (initial.longBodyCharacters < 30000 || initial.longBodyCharacters > 50000 || initial.omittedControls !== 1) throw new Error('Long message was not progressively rendered');
    if (initial.documentOverflow > 1 || initialRenderMs > 7000) throw new Error(`Long session initial render is unstable: ${initialRenderMs}ms`);

    const expandStarted = Date.now();
    await last.locator('.long-message-control button').first().click();
    await page.waitForFunction(() => (document.querySelector('[data-message-id="message-0419"] .message-body')?.innerText.length || 0) > 70000);
    const expandMs = Date.now() - expandStarted;
    const expandedCharacters = await last.locator('.message-body').innerText().then(value => value.length);
    if (expandedCharacters > 95000 || expandMs > 3000) throw new Error(`Progressive expansion exceeded budget: ${expandedCharacters} chars / ${expandMs}ms`);

    await page.evaluate(() => { const el = document.querySelector('.conversation'); el.style.scrollBehavior = 'auto'; el.scrollTop = 0; });
    const anchorBefore = await page.locator('[data-message-id="message-0340"]').evaluate(node => node.getBoundingClientRect().top);
    await page.evaluate(() => document.querySelector('.history-window-control button')?.click());
    await page.waitForFunction(() => document.querySelector('.conversation>.message')?.dataset.messageId === 'message-0280');
    const shifted = await page.evaluate(() => {
      const articles = [...document.querySelectorAll('.conversation>.message')];
      return { count: articles.length, firstId: articles[0]?.dataset.messageId || '', lastId: articles.at(-1)?.dataset.messageId || '', anchorTop: document.querySelector('[data-message-id="message-0340"]')?.getBoundingClientRect().top || 0 };
    });
    if (shifted.count > 80 || shifted.firstId !== 'message-0280' || shifted.lastId !== 'message-0359') throw new Error('Overlapping history window is incorrect');
    if (Math.abs(shifted.anchorTop - anchorBefore) > 4) throw new Error(`Scroll anchor moved by ${Math.abs(shifted.anchorTop - anchorBefore)}px`);

    await page.evaluate(() => {
      const body = document.querySelector('[data-message-id="message-0340"] .message-body');
      const text = body?.querySelector('span')?.firstChild;
      if (!body || !text) throw new Error('Selection fixture missing');
      const range = document.createRange();
      range.setStart(text, 0); range.setEnd(text, Math.min(8, text.textContent.length));
      const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
      body.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 420, clientY: 260 }));
    });
    await page.locator('.selection-menu').waitFor({ state: 'visible' });
    const selectionActions = await page.locator('.selection-menu button').allTextContents();
    if (selectionActions.join('|') !== '复制|引用|导出 TXT') throw new Error('Selection actions changed under virtualization');

    await page.screenshot({ path: path.join(outputDir, 'long-session-ui.png') });
    process.stdout.write(JSON.stringify({ longSessionUi: 'PASS', totalMessages: messages.length, totalCharacters: messages.reduce((sum, message) => sum + message.text.length, 0), initialRenderMs, expandMs, initial, shifted, selectionActions }, null, 2));
  } finally { await browser.close(); }
}

main().catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });

const { chromium } = require('playwright');

const base = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const browserExe = process.env.CLAUDE_UI_BROWSER_EXE;
if (!base || !secret || !browserExe) throw new Error('New-task draft UI environment is incomplete');

const proxy = () => {
  const root = document.querySelector('#app');
  return root.__vueParentComponent?.proxy || root._vnode?.component?.proxy || root.__vue_app__?._container?._vnode?.component?.proxy;
};

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: browserExe });
  try {
    const context = await browser.newContext({
      viewport: { width: 1000, height: 760 },
      extraHTTPHeaders: { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' }
    });
    const page = await context.newPage();
    await page.goto(base, { waitUntil: 'domcontentloaded' });
    await page.locator('.new-chat').waitFor({ state: 'attached' });
    try {
      await page.waitForFunction(() => document.documentElement.dataset.bootReady === 'true', null, { timeout: 30000 });
    } catch (error) {
      const startup = await page.evaluate(() => {
        const root = document.querySelector('#app');
        const vm = root?.__vueParentComponent?.proxy || root?._vnode?.component?.proxy || root?.__vue_app__?._container?._vnode?.component?.proxy;
        return { dataset: document.documentElement.dataset.bootReady || '', className: root?.className || '', bootReady: vm?.bootReady, status: vm?.status, connectionState: vm?.connectionState };
      });
      throw new Error('Workbench did not expose its first ready frame: ' + JSON.stringify(startup), { cause: error });
    }
    const readyFrame = await page.evaluate(() => ({ root: document.querySelector('#app')?.outerHTML.slice(0,180), visibility: getComputedStyle(document.querySelector('#app')).visibility, display: getComputedStyle(document.querySelector('#app')).display }));
    if (readyFrame.visibility !== 'visible') throw new Error('Ready frame stayed hidden: ' + JSON.stringify(readyFrame));
    await page.locator('.new-chat').waitFor({ state: 'visible' });

    await page.getByRole('button', { name: '定时任务' }).click();
    await page.locator('.schedule-workspace-page').waitFor();
    await page.locator('.new-chat').click();
    await page.locator('.composer textarea').waitFor();
    await page.locator('.composer textarea').fill('这个草稿不能因重复点击而消失');

    await page.getByRole('button', { name: '插件' }).click();
    await page.locator('.plugin-workspace-page').waitFor();
    await page.locator('.new-chat').click();
    const first = await page.evaluate(proxy => {
      const vm = eval(`(${proxy})`)();
      return { view: vm.view, id: vm.activeSessionId, prompt: vm.prompt, visible: vm.sortedSessions.length, total: vm.sessions.length, draftOnly: vm.currentSession?.draftOnly };
    }, proxy.toString());

    await page.locator('.new-chat').click();
    const second = await page.evaluate(proxy => {
      const vm = eval(`(${proxy})`)();
      return { view: vm.view, id: vm.activeSessionId, prompt: vm.prompt, visible: vm.sortedSessions.length, total: vm.sessions.length };
    }, proxy.toString());

    const published = await page.evaluate(async proxy => {
      const vm = eval(`(${proxy})`)();
      const writes = [];
      vm.settings.workerHarness = 'codex';
      vm.ensureTaskWorkspace = async session => session.workspace;
      vm.previewContextNow = async () => ({ decision: 'allowed' });
      vm.api = async (path, options = {}) => {
        if (path === '/api/sessions') writes.push(options.body);
        if (path === '/api/chat/start') return { jobId: 'draft-publication-test', eventCursor: 0 };
        if (path === '/api/chat/runs') return [];
        return [];
      };
      await vm.sendPrompt();
      clearTimeout(vm.pollHandle);
      vm.activeJob = '';
      return {
        visible: vm.sortedSessions.length,
        draftOnly: !!vm.currentSession?.draftOnly,
        publishedAt: vm.currentSession?.publishedAt || '',
        persisted: writes.some(body => Array.isArray(body) && body.some(item => item.id === vm.activeSessionId))
      };
    }, proxy.toString());

    if (first.view !== 'chat' || first.prompt !== '这个草稿不能因重复点击而消失' || first.visible !== 0 || first.total !== 1 || first.draftOnly !== true) {
      throw new Error('Workspace page did not return to a hidden new-task draft: ' + JSON.stringify(first));
    }
    if (second.id !== first.id || second.prompt !== first.prompt || second.total !== first.total || second.visible !== 0) {
      throw new Error('Repeated New Task cleared or duplicated the draft: ' + JSON.stringify({ first, second }));
    }
    if (published.visible !== 1 || published.draftOnly || !published.publishedAt || !published.persisted) {
      throw new Error('First submission did not publish and persist the task: ' + JSON.stringify(published));
    }
    process.stdout.write(JSON.stringify({ newTaskDraftUi: 'PASS', first, second, published }, null, 2));
    await context.close();
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });

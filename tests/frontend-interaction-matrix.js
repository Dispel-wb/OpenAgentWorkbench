const { chromium } = require('playwright');

const base = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const browserExe = process.env.CLAUDE_UI_BROWSER_EXE;
if (!base || !secret || !browserExe) throw new Error('Frontend interaction environment is incomplete');

const proxySource = `() => {
  const root=document.querySelector('#app');
  return root.__vueParentComponent?.proxy||root._vnode?.component?.proxy||root.__vue_app__?._container?._vnode?.component?.proxy;
}`;

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: browserExe });
  try {
    const context = await browser.newContext({
      viewport: { width: 1200, height: 840 },
      extraHTTPHeaders: { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' }
    });
    const page = await context.newPage();
    await page.goto(base, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => document.documentElement.dataset.interfaceReady === 'true', null, { timeout: 30000 });

    // Workspace navigation and drawer lifecycle must work through real clicks.
    await page.getByRole('button', { name: '定时任务' }).click();
    await page.locator('.schedule-workspace-page').waitFor();
    await page.getByRole('button', { name: '创建定时任务' }).click();
    await page.locator('[aria-label="定时任务编辑"]').waitFor();
    await page.locator('[aria-label="定时任务编辑"] button[aria-label="关闭"]').click();
    if (await page.locator('[aria-label="定时任务编辑"]').count()) throw new Error('Schedule drawer did not close');

    await page.getByRole('button', { name: '插件' }).click();
    await page.locator('.plugin-workspace-page').waitFor();
    await page.evaluate(async source => { const vm = eval(source)(); await Promise.all([vm.loadExtensions(), vm.loadDshCatalog()]); }, proxySource);
    await page.waitForTimeout(500);
    const beforeTabs = await page.evaluate(source => { const vm=eval(source)(); return { view:vm.view, pluginTab:vm.pluginTab, page:!!document.querySelector('.plugin-workspace-page'), tabs:[...document.querySelectorAll('.workspace-page-tabs button')].map(x=>x.textContent.trim()) }; }, proxySource);
    if (beforeTabs.view !== 'plugins' || !beforeTabs.page) throw new Error('Plugin page changed during loading: ' + JSON.stringify(beforeTabs));
    for (const tab of ['技能', 'MCP 库', '插件']) {
      await page.locator('.plugin-workspace-page .workspace-page-tabs').getByRole('button', { name: tab, exact: true }).click({ timeout: 5000 });
      const active = await page.locator('.workspace-page-tabs button.active').innerText();
      if (active.trim() !== tab) throw new Error(`Plugin tab did not activate: ${tab}`);
    }

    await page.evaluate(async source => {
      const vm = eval(source)();
      vm.extensions.skills = Array.from({ length: 9 }, (_, i) => ({ name: `交互技能 ${i + 1}`, description: `说明 ${i + 1}`, path: `C:\\fixture\\skill-${i + 1}.md`, scope: 'shared', active: true }));
      vm.pluginSearch = '';
      vm.pluginExpanded = false;
    }, proxySource);
    await page.locator('.workspace-page-tabs').getByRole('button', { name: '技能', exact: true }).click();
    await page.getByRole('button', { name: /查看全部 9 项/ }).click();
    if (await page.locator('.catalog-expand').filter({ hasText: '收起' }).count() !== 1) throw new Error('Skill catalog did not expand');
    await page.locator('.catalog-grid .catalog-item').filter({ hasText: '交互技能 1' }).click();
    await page.locator('[aria-label="扩展详情"]').waitFor();
    await page.locator('[aria-label="扩展详情"] button[aria-label="关闭"]').click();
    await page.getByRole('button', { name: '收起', exact: true }).click();
    if (await page.getByRole('button', { name: /查看全部 9 项/ }).count() !== 1) throw new Error('Skill catalog did not collapse again');

    // Seed a durable conversation and exercise every right-click action.
    const seeded = await page.evaluate(source => {
      const vm = eval(source)();
      const now = new Date().toISOString();
      const session = { id: crypto.randomUUID(), claudeSessionId: crypto.randomUUID(), title: '右键交互测试', kind: 'chat', createdAt: now, updatedAt: now, workspace: vm.workspace, workspaceRoot: vm.workspaceRoot, started: true, allowedDirs: [], queue: [] };
      vm.sessions.push(session);
      return session.id;
    }, proxySource);
    await page.locator('.new-chat').click();
    const row = page.locator('.session-entry').filter({ hasText: '右键交互测试' });
    await row.click({ button: 'right' });
    await page.getByRole('menuitem', { name: /重命名/ }).click();
    await page.locator('.session-rename-input').fill('已重命名交互测试');
    await page.locator('.session-rename-form').getByRole('button', { name: '保存' }).click();
    await page.locator('.session-entry').filter({ hasText: '已重命名交互测试' }).waitFor();

    const renamedRow = page.locator('.session-entry').filter({ hasText: '已重命名交互测试' });
    await renamedRow.click({ button: 'right' });
    await page.getByRole('menuitem', { name: /添加星标/ }).click();
    if (!(await renamedRow.locator('.session-pin').innerText()).includes('★')) throw new Error('Session pin click did not update the row');
    await renamedRow.click({ button: 'right' });
    await page.getByRole('menuitem', { name: /添加定时任务/ }).click();
    await page.locator('[aria-label="定时任务编辑"]').waitFor();
    const scheduleTarget = await page.evaluate(source => eval(source)().scheduleEditor.targetSessionId, proxySource);
    if (scheduleTarget !== seeded) throw new Error('Right-click schedule targeted the wrong conversation');
    await page.locator('[aria-label="定时任务编辑"] button[aria-label="关闭"]').click();
    await page.locator('.new-chat').click();
    await page.locator('.session-entry').filter({ hasText: '已重命名交互测试' }).click({ button: 'right' });
    await page.getByRole('menuitem', { name: /归档任务/ }).click();
    if (await page.locator('.session-entry').filter({ hasText: '已重命名交互测试' }).count()) throw new Error('Archived conversation remained in the sidebar');

    await page.getByRole('button', { name: '打开工作台菜单' }).click();
    await page.getByRole('button', { name: '设置', exact: true }).click();
    await page.getByRole('dialog').waitFor();
    await page.locator('.settings-nav').getByRole('button', { name: /已归档聊天/ }).click();
    const archivedRow = page.locator('.archive-row').filter({ hasText: '已重命名交互测试' });
    await archivedRow.getByRole('button', { name: '恢复' }).click();
    if (await page.locator('.archive-row').filter({ hasText: '已重命名交互测试' }).count()) throw new Error('Restored conversation remained archived');
    await page.getByRole('button', { name: '关闭设置' }).click();
    const restoredRow = page.locator('.session-entry').filter({ hasText: '已重命名交互测试' });
    await restoredRow.click({ button: 'right' });
    await page.getByRole('menuitem', { name: /归档任务/ }).click();
    await page.getByRole('button', { name: '打开工作台菜单' }).click();
    await page.getByRole('button', { name: '设置', exact: true }).click();
    await page.locator('.settings-nav').getByRole('button', { name: /已归档聊天/ }).click();
    page.once('dialog', dialog => dialog.accept());
    const deleteRow = page.locator('.archive-row').filter({ hasText: '已重命名交互测试' });
    await deleteRow.getByRole('button', { name: '删除' }).click();
    try { await deleteRow.waitFor({ state: 'detached', timeout: 10000 }); }
    catch { const status=await page.evaluate(source=>eval(source)().status,proxySource); throw new Error('Deleted archive remained visible: '+status); }

    await page.locator('.settings-nav').getByRole('button', { name: /API 设置/ }).click();
    await page.locator('.new-provider').click();
    if (!(await page.locator('.provider-editor-dialog input[type="password"]').isVisible())) throw new Error('Add API did not open the isolated token editor');
    await page.getByRole('button', { name: '取消', exact: true }).click();
    await page.getByRole('button', { name: '关闭设置' }).click();

    // Process, individual thought/command blocks and long narrative can all fold both ways.
    await page.evaluate(source => {
      const vm = eval(source)();
      vm.view = 'chat';
      vm.messages = [{
        id: crypto.randomUUID(), role: 'assistant', text: '## 最终统一结果\n\n| 项目 | 状态 |\n|---|---|\n| 点击 | 通过 |\n\n`inline`\n\n> 主题引用\n\n```js\nconst ok = true;\n```',
        streaming: false, processOpen: true, variantState: 'completed',
        workflow: [
          { id: 'n1', kind: 'narrative', title: '阶段思考', detail: '阶段思考 '.repeat(300), expanded: false, state: 'done' },
          { id: 'c1', kind: 'command', title: '执行前端验证', rawName: 'Bash', input: 'node test.js', output: 'PASS', open: false, state: 'done' }
        ]
      }];
    }, proxySource);
    const processSummary = page.locator('.process-summary');
    await processSummary.waitFor();
    if (!(await processSummary.getAttribute('aria-controls'))) throw new Error('Process disclosure is not associated with its panel');
    await processSummary.click();
    if ((await processSummary.getAttribute('aria-expanded')) !== 'false') throw new Error('Whole process did not collapse');
    if (!(await page.locator('.final-answer-body').isVisible())) throw new Error('Final answer disappeared with the process');
    await processSummary.click();
    if ((await processSummary.getAttribute('aria-expanded')) !== 'true') throw new Error('Whole process did not reopen');
    const commandSummary = page.locator('.workflow-summary');
    if (!(await commandSummary.getAttribute('aria-controls'))) throw new Error('Command disclosure is not associated with its panel');
    await commandSummary.click();
    if ((await commandSummary.getAttribute('aria-expanded')) !== 'true') throw new Error('Command block did not open');
    await commandSummary.click();
    if ((await commandSummary.getAttribute('aria-expanded')) !== 'false') throw new Error('Command block did not close');
    const narrativeToggle = page.locator('.workflow-narrative button');
    const narrativeBody = page.locator('.workflow-narrative .markdown-body');
    if ((await narrativeBody.getAttribute('aria-hidden')) !== 'true') throw new Error('Collapsed long narrative must stay out of the screen-reader live region');
    await narrativeToggle.click();
    if ((await narrativeToggle.getAttribute('aria-expanded')) !== 'true') throw new Error('Narrative accessibility state did not expand');
    if ((await narrativeBody.getAttribute('aria-hidden')) !== 'false') throw new Error('Expanded narrative must be exposed to screen readers');
    if ((await narrativeToggle.innerText()).trim() !== '收起本段') throw new Error('Narrative did not expand');
    await narrativeToggle.click();
    if ((await narrativeToggle.innerText()).trim() !== '展开本段') throw new Error('Narrative did not collapse');
    if ((await narrativeToggle.getAttribute('aria-expanded')) !== 'false') throw new Error('Narrative accessibility state did not collapse');
    if ((await narrativeBody.getAttribute('aria-hidden')) !== 'true') throw new Error('Re-collapsed narrative must leave the screen-reader live region');

    const markdown = page.locator('.final-answer-body');
    for (const selector of ['table', 'code', 'blockquote', 'pre']) if (!(await markdown.locator(selector).count())) throw new Error(`Markdown renderer missed ${selector}`);
    const wrap = await markdown.locator('pre').evaluate(node => getComputedStyle(node).whiteSpace);
    if (!['pre-wrap', 'break-spaces'].includes(wrap)) throw new Error(`Code block does not wrap: ${wrap}`);

    // Theme/skin matrix: every local skin in light/dark must produce visible themed blocks.
    const matrix = await page.evaluate(source => {
      const vm = eval(source)();
      const rows = [];
      for (const skin of ['claude', 'codex', 'fusion']) for (const theme of ['light', 'dark']) {
        vm.settings.skin = skin; vm.settings.theme = theme; vm.applyAppearance();
        const quote = document.querySelector('.final-answer-body blockquote');
        const code = document.querySelector('.final-answer-body code');
        const qs = getComputedStyle(quote), cs = getComputedStyle(code);
        rows.push({ skin, theme, quoteBg: qs.backgroundColor, quoteColor: qs.color, codeBg: cs.backgroundColor, codeColor: cs.color });
      }
      return rows;
    }, proxySource);
    for (const row of matrix) {
      if (row.quoteBg === 'rgba(0, 0, 0, 0)' || row.codeBg === 'rgba(0, 0, 0, 0)' || row.quoteColor === row.quoteBg || row.codeColor === row.codeBg)
        throw new Error('Theme block lost contrast: ' + JSON.stringify(row));
    }

    await page.screenshot({ path: process.env.CLAUDE_UI_SCREENSHOT, fullPage: true });
    process.stdout.write(JSON.stringify({ frontendInteractionMatrix: 'PASS', navigation: true, drawers: true, pluginExpandCollapse: true, sessionContextMenu: true, archiveRestoreDelete: true, providerAddIsolation: true, processFold: true, nestedFold: true, markdown: true, themeRows: matrix.length }, null, 2));
    await context.close();
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

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
    await page.locator('.footer-menu-trigger').click();
    await page.locator('.footer-menu-popover button[aria-label="设置"]').click();
    await page.locator('.provider-modal').waitFor({ state: 'visible' });
    const card = page.locator('.provider-library-list article').filter({ hasText: provider.name });
    await card.waitFor({ state: 'visible' });
    const listText = await card.textContent();
    if (!listText.includes('令牌已加密保存') || listText.includes(provider.token)) throw new Error('Saved provider list does not keep the token private');
    await card.locator('button').filter({ hasText: '配置模型' }).click();
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
      passwordPresent: !!document.querySelector('input[type="password"]'),
      tokenValue: document.querySelector('input[type="password"]')?.value || '',
      encryptedState: document.querySelector('.provider-secret-state')?.textContent || ''
    }));
    if (!result.panelVisible || !result.dotVisible || result.panelWidth < 240) throw new Error(label + ': health evidence is not visible');
    if (result.matrixWidth < 240 || result.matrixOverflow > 1) throw new Error(label + ': capability matrix is missing or overflowing');
    if (result.documentWidth > result.viewport + 1) throw new Error(label + ': horizontal overflow');
    if (result.passwordPresent || result.tokenValue || !result.encryptedState.includes('令牌已加密保存')) throw new Error(label + ': saved token was exposed or model settings lacks encrypted state');
    return result;
  };

  await openProviderSettings();
  const dark = await assertLayout('dark');
  await page.screenshot({ path: path.join(outputDir, 'provider-health-ui-640-dark.png'), fullPage: false });
  await page.locator('.model-capability-matrix').screenshot({ path: path.join(outputDir, 'model-capability-ui-640-dark.png') });

  await page.locator('.provider-editor-back').click();
  const savedCard = page.locator('.provider-library-list article').filter({ hasText: provider.name });
  const savedActions = await savedCard.locator('button').allTextContents();
  if (!['配置模型', '更换令牌', '删除'].every(label => savedActions.some(value => value.includes(label)))) throw new Error('Provider library actions are not separated');
  await page.screenshot({ path: path.join(outputDir, 'provider-library-dark.png'), fullPage: false });
  await savedCard.locator('button').filter({ hasText: '更换令牌' }).click();
  const replacement = page.locator('.provider-editor-dialog input[type="password"]');
  await replacement.waitFor({ state: 'visible' });
  if (await replacement.inputValue()) throw new Error('Token replacement field must start empty');
  if (await page.locator('.capability-card').count()) throw new Error('Token replacement must be separate from model editing');
  await page.screenshot({ path: path.join(outputDir, 'provider-token-replace-dark.png'), fullPage: false });
  await page.locator('.provider-editor-back').click();
  await page.locator('.api-library-title .primary').click();
  const addToken = page.locator('.provider-editor-dialog input[type="password"]');
  await addToken.fill(provider.token);
  await page.locator('.provider-editor-dialog input').first().fill('重复令牌演示');
  await page.locator('.provider-modal footer .primary').click();
  await page.locator('.provider-duplicate-notice').waitFor({ state: 'visible' });
  const duplicateText = await page.locator('.provider-duplicate-notice').textContent();
  if (!duplicateText.includes('已经添加过') || !duplicateText.includes('配置模型')) throw new Error('Duplicate token does not redirect the user to model settings');
  await page.screenshot({ path: path.join(outputDir, 'provider-duplicate-warning-dark.png'), fullPage: false });
  await page.locator('.provider-duplicate-notice button').click();
  if (await page.locator('.provider-editor-dialog input[type="password"]').count()) throw new Error('Duplicate redirect exposed a password field in model settings');

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

  await page.locator('.settings-page .modal-close').click();
  await page.evaluate(() => {
    const conversation = document.querySelector('.conversation');
    conversation.innerHTML = '<article class="message assistant"><div class="message-column"><div class="markdown-body"><h2>操作与验证</h2><p>使用 <code class="inline-code">node --check</code> 棭验脚本，再打开 <code class="inline-code">tetris.html</code>。</p><div class="md-table-wrap"><table><thead><tr><th>项目</th><th>说明</th></tr></thead><tbody><tr><td>旋转</td><td>支持顺时针与逆时针</td></tr><tr><td>计分</td><td>按消除行数累计分数</td></tr><tr><td>预览</td><td>显示后续三个方块</td></tr></tbody></table></div></div></div></article>';
  });
  const markdownSkins = {};
  const markdownDarkColors = {};
  for (const activeSkin of ['codex', 'fusion', 'claude']) {
    await page.evaluate(value => { document.documentElement.dataset.skin = value; document.documentElement.dataset.theme = 'dark'; }, activeSkin);
    markdownDarkColors[activeSkin] = await page.evaluate(() => getComputedStyle(document.querySelector('.inline-code')).color);
    await page.evaluate(value => { document.documentElement.dataset.skin = value; document.documentElement.dataset.theme = 'light'; }, activeSkin);
    markdownSkins[activeSkin] = await page.evaluate(() => {
      const inline = getComputedStyle(document.querySelector('.inline-code'));
      const head = getComputedStyle(document.querySelector('.markdown-body th'));
      const row = getComputedStyle(document.querySelector('.markdown-body td'));
      return { inlineFontSize: parseFloat(inline.fontSize), inlineHeight: document.querySelector('.inline-code').getBoundingClientRect().height, inlinePadding: inline.padding, inlineBackground: inline.backgroundColor, inlineColor: inline.color, tableHead: head.backgroundColor, tableRow: row.backgroundColor };
    });
    if (markdownSkins[activeSkin].inlineHeight < 26 || !markdownSkins[activeSkin].inlinePadding.includes('8px')) throw new Error(activeSkin + ': inline Markdown container remains too small');
    await page.locator('.message.assistant').screenshot({ path: path.join(outputDir, `markdown-${activeSkin}-light.png`) });
  }
  if (markdownSkins.codex.inlineBackground === markdownSkins.fusion.inlineBackground) throw new Error('Codex and hybrid inline Markdown still share the same detached skin');
  if (markdownDarkColors.codex !== 'rgb(134, 173, 221)' || markdownDarkColors.fusion !== markdownDarkColors.codex) throw new Error('Dark Codex and hybrid inline code text must share the brighter deep-blue color');
  if (markdownSkins.claude.inlineBackground !== markdownSkins.codex.inlineBackground || markdownSkins.claude.inlineColor !== markdownSkins.codex.inlineColor || markdownSkins.codex.inlineColor !== markdownSkins.fusion.inlineColor) throw new Error('Light Claude must use the Codex inline container and all light skins must use the same blue-gray code text');
  if (markdownSkins.claude.tableHead === markdownSkins.claude.tableRow) throw new Error('Claude table lost its restrained header hierarchy');

  process.stdout.write(JSON.stringify({ providerHealthUi: 'PASS', dark, narrow, light, markdownSkins, markdownDarkColors, separatedFlows: true, duplicateRedirect: true, tokenLeaked: false }, null, 2));
} finally {
  await browser.close();
}
}

main().catch(error => {
  process.stderr.write((error && error.stack) || String(error));
  process.exitCode = 1;
});

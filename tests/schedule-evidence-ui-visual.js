const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const baseUrl = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const browserExecutable = process.env.CLAUDE_UI_BROWSER_EXE;
const outputDir = process.env.CLAUDE_UI_OUTPUT_DIR;
if (!baseUrl || !secret || !browserExecutable || !outputDir) throw new Error('Schedule evidence visual environment is incomplete');
fs.mkdirSync(outputDir, { recursive: true });

const protocolHeaders = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };

async function setTheme(request, theme) {
  const bootstrapResponse = await request.get(`${baseUrl}/api/bootstrap`, { headers: protocolHeaders });
  if (!bootstrapResponse.ok()) throw new Error(`Bootstrap failed before ${theme} theme: ${bootstrapResponse.status()}`);
  const bootstrap = await bootstrapResponse.json();
  const response = await request.post(`${baseUrl}/api/settings`, {
    headers: protocolHeaders,
    data: { ...(bootstrap.settings || {}), theme }
  });
  if (!response.ok()) throw new Error(`Theme update failed for ${theme}: ${response.status()}`);
}

async function openEvidence(page) {
  await page.locator('.sidebar-actions > button').first().click();
  await page.locator('.inspector nav button').nth(8).click();
  const evidenceButton = page.locator('.schedule-evidence-action').first();
  await evidenceButton.waitFor({ state: 'visible' });
  await evidenceButton.click();
  await page.locator('.schedule-evidence-modal').waitFor({ state: 'visible' });
  await page.locator('.schedule-evidence-kpis').waitFor({ state: 'visible' });
}

async function measure(page) {
  return page.evaluate(() => {
    const modal = document.querySelector('.schedule-evidence-modal');
    const scroll = document.querySelector('.schedule-evidence-scroll');
    const actions = document.querySelector('.schedule-actions');
    const visibleChildren = scroll ? [...scroll.children].filter(child => {
      const style = getComputedStyle(child);
      const rect = child.getBoundingClientRect();
      return style.display !== 'none' && rect.height > 0;
    }) : [];
    const scrollRect = scroll?.getBoundingClientRect();
    const contentBottom = visibleChildren.length ? Math.max(...visibleChildren.map(child => child.getBoundingClientRect().bottom)) : scrollRect?.top || 0;
    return {
      theme: document.documentElement.dataset.theme || '',
      viewportWidth: window.innerWidth,
      documentWidth: document.documentElement.scrollWidth,
      modalWidth: modal?.getBoundingClientRect().width || 0,
      modalHeight: modal?.getBoundingClientRect().height || 0,
      modalOverflowX: modal ? modal.scrollWidth - modal.clientWidth : 0,
      scrollOverflowX: scroll ? scroll.scrollWidth - scroll.clientWidth : 0,
      blankSpace: scrollRect ? Math.max(0, scrollRect.bottom - contentBottom) : 0,
      actionsHeight: actions?.getBoundingClientRect().height || 0,
      runId: document.querySelector('.schedule-evidence-modal footer code')?.textContent?.trim() || ''
    };
  });
}

function assertLayout(layout, expectedTheme, narrow = false) {
  if (layout.theme !== expectedTheme) throw new Error(`Expected ${expectedTheme} theme, received ${layout.theme || 'unset'}`);
  if (layout.documentWidth > layout.viewportWidth + 1) throw new Error('Schedule evidence page has horizontal overflow');
  if (layout.modalOverflowX > 1 || layout.scrollOverflowX > 1) throw new Error('Schedule evidence content overflows horizontally');
  if (layout.modalHeight < 210 || layout.modalHeight > 705) throw new Error(`Schedule evidence modal height is invalid: ${layout.modalHeight}`);
  if (!narrow && layout.modalWidth < 500) throw new Error('Schedule evidence modal is not stably sized');
  if (narrow && layout.modalWidth > layout.viewportWidth) throw new Error('Schedule evidence narrow modal exceeds the viewport');
  if (layout.blankSpace > 48) throw new Error(`Schedule evidence modal leaves excessive blank space: ${layout.blankSpace}px`);
  if (layout.actionsHeight < 20 || !layout.runId) throw new Error('Schedule actions or Run linkage are not visible');
}

async function main() {
  const browser = await chromium.launch({ headless: true, executablePath: browserExecutable });
  try {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 900 },
      deviceScaleFactor: 1,
      extraHTTPHeaders: protocolHeaders
    });
    const page = await context.newPage();

    await setTheme(context.request, 'dark');
    await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    await openEvidence(page);
    const dark = await measure(page);
    assertLayout(dark, 'dark');
    await page.screenshot({ path: path.join(outputDir, 'schedule-evidence-ui-640-dark.png'), fullPage: false });

    await page.setViewportSize({ width: 920, height: 760 });
    const narrow = await measure(page);
    assertLayout(narrow, 'dark', true);
    await page.screenshot({ path: path.join(outputDir, 'schedule-evidence-ui-640-narrow.png'), fullPage: false });

    await setTheme(context.request, 'light');
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await openEvidence(page);
    const light = await measure(page);
    assertLayout(light, 'light');
    await page.screenshot({ path: path.join(outputDir, 'schedule-evidence-ui-640-light.png'), fullPage: false });

    console.log(JSON.stringify({ scheduleEvidenceVisual: 'PASS', dark, narrow, light }, null, 2));
  } finally {
    await browser.close();
  }
}

main().catch(error => { console.error(error); process.exit(1); });

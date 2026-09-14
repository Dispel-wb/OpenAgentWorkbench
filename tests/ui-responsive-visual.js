const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const baseUrl = process.env.CLAUDE_UI_BASE_URL;
const secret = process.env.CLAUDE_UI_SECRET;
const outputDir = process.env.CLAUDE_UI_OUTPUT_DIR;
const browserExecutable = process.env.CLAUDE_UI_BROWSER_EXE;
const workspace = process.env.CLAUDE_UI_WORKSPACE;
if (!baseUrl || !secret || !outputDir || !browserExecutable || !workspace) throw new Error('Responsive UI visual environment is incomplete');
fs.mkdirSync(outputDir, { recursive: true });
const headers = { 'X-Desktop-Secret': secret, 'X-Workbench-Protocol': '2' };

async function main() {
  const browser = await chromium.launch({ headless: true, executablePath: browserExecutable });
  try {
    const context = await browser.newContext({ viewport: { width: 1280, height: 780 }, extraHTTPHeaders: headers });
    const request = context.request;
    const provider = await request.post(baseUrl + '/api/providers', { data: {
      id: 'responsive-local', name: 'SiliconFlow Local Fixture', token: 'sk-offline-layout-only', authStyle: 'bearer',
      text: { enabled: true, protocol: 'openai', baseUrl: 'http://127.0.0.1:9/v1', models: ['deepseek-ai/DeepSeek-V4-Flash-Long-Model-Name'] },
      image: { enabled: false, protocol: 'openai-images', baseUrl: '', models: [] }
    }});
    if (!provider.ok()) throw new Error('Unable to seed responsive provider');
    const memory = await request.post(baseUrl + '/api/workbench/memories', { data: {
      workspace, title: '默认语言与输出习惯', content: '默认使用中文回答；技术专有名词保留英文。', active: true
    }});
    if (!memory.ok()) throw new Error('Unable to seed workspace memory');
    await request.post(baseUrl + '/api/settings', { data: {
      workspace, theme: 'dark', skin: 'fusion', providerId: 'responsive-local', model: 'deepseek-ai/DeepSeek-V4-Flash-Long-Model-Name',
      effort: 'max', permissionMode: 'agent', allowedTools: 'Read,Glob,Grep,Skill', disallowedTools: ''
    }});

    const page = await context.newPage();
    await page.route('**/api/chat/runs', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{id:'fixture-background-run',kind:'chat',sessionId:'fixture-unopened-session',workspace,state:'running',isActive:true,recovered:false,eventCursor:0,eventCount:12,pendingApprovals:1,startedAt:new Date(Date.now()-83000).toISOString(),elapsedMs:83000,providerId:'responsive-local',model:'deepseek-ai/DeepSeek-V4-Flash-Long-Model-Name'}]) }));
    await page.route('**/api/permissions/pending', route => route.fulfill({status:200,contentType:'application/json',body:JSON.stringify([{id:'fixture-approval',jobId:'fixture-background-run',sessionId:'fixture-unopened-session',workspace,model:'deepseek-ai/DeepSeek-V4-Flash-Long-Model-Name',toolName:'Bash',capability:'execute',risk:'high',reason:'需要一次性确认后台命令。',input:{command:'echo visual-fixture'},createdAt:new Date().toISOString()}])}));
    await page.route('**/api/workbench/activity?*',async route=>{await new Promise(resolve=>setTimeout(resolve,900));await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({revision:Date.now(),changed:true,heartbeat:false,runs:[{id:'fixture-background-run',kind:'chat',sessionId:'fixture-unopened-session',workspace,state:'running',isActive:true,recovered:false,eventCursor:0,eventCount:12,pendingApprovals:1,startedAt:new Date(Date.now()-83000).toISOString(),elapsedMs:83000,providerId:'responsive-local',model:'deepseek-ai/DeepSeek-V4-Flash-Long-Model-Name'}],approvals:[{id:'fixture-approval',jobId:'fixture-background-run',sessionId:'fixture-unopened-session',workspace,model:'deepseek-ai/DeepSeek-V4-Flash-Long-Model-Name',toolName:'Bash',capability:'execute',risk:'high',reason:'需要一次性确认后台命令。',input:{command:'echo visual-fixture'},createdAt:new Date().toISOString()}],queue:[]})});});
    await page.goto(baseUrl, { waitUntil: 'domcontentloaded' });
    await page.locator('.provider-selector select').waitFor({ state: 'visible' });
    await page.locator('.run-center-trigger em').waitFor({ state: 'attached' });
    await page.locator('.approval-card').waitFor({state:'visible'});

    const results = [];
    for (const viewport of [{ width: 1280, height: 780, label: 'desktop' }, { width: 826, height: 720, label: 'high-dpi' }, { width: 620, height: 680, label: 'minimum' }]) {
      await page.setViewportSize(viewport);
      await page.waitForTimeout(120);
      const result = await page.evaluate(() => {
        const visibleRect = selector => {
          const element = document.querySelector(selector);
          const rect = element?.getBoundingClientRect();
          const style = element ? getComputedStyle(element) : null;
          return { present: !!element, visible: !!rect && style.display !== 'none' && rect.width > 0 && rect.height > 0,
            left: rect?.left || 0, right: rect?.right || 0, top: rect?.top || 0, width: rect?.width || 0, height: rect?.height || 0 };
        };
        const topbar = document.querySelector('.topbar');
        const provider = visibleRect('.provider-selector');
        const model = visibleRect('.model-selector');
        const providerSelect = document.querySelector('.provider-selector select');
        const modelSelect = document.querySelector('.model-selector select');
        const conversation = document.querySelector('.conversation');
        const composer = document.querySelector('.composer');
        const composerText = document.querySelector('.composer textarea');
        const newChat = document.querySelector('.new-chat');
        newChat?.focus();
        const conversationStyle = conversation ? getComputedStyle(conversation) : null;
        const newChatStyle = newChat ? getComputedStyle(newChat) : null;
        return {
          viewport: window.innerWidth,
          documentWidth: document.documentElement.scrollWidth,
          topbarOverflow: topbar ? topbar.scrollWidth - topbar.clientWidth : 999,
          provider, model,
          gap: model.left - provider.right,
          providerValue: providerSelect?.selectedOptions?.[0]?.textContent?.trim() || '',
          providerTitle: providerSelect?.title || '',
          modelValue: modelSelect?.value || '',
          modelTitle: modelSelect?.title || '',
          conversationInset: Number.parseFloat(conversationStyle?.paddingLeft || '0'),
          composerHeight: composer?.getBoundingClientRect().height || 0,
          composerTextHeight: composerText?.getBoundingClientRect().height || 0,
          newChatHeight: newChat?.getBoundingClientRect().height || 0,
          newChatBorderWidth: Number.parseFloat(newChatStyle?.borderTopWidth || '99'),
          newChatOutlineWidth: Number.parseFloat(newChatStyle?.outlineWidth || '99')
        };
      });
      if (!result.provider.visible || !result.model.visible) throw new Error(viewport.label + ': primary selectors are not visible');
      if (result.provider.width < 100 || result.model.width < 125) throw new Error(viewport.label + ': primary selectors are too narrow');
      if (result.gap < 4) throw new Error(viewport.label + ': primary selectors overlap');
      if (result.documentWidth > result.viewport + 1 || result.topbarOverflow > 1) throw new Error(viewport.label + ': horizontal overflow');
      if (result.providerValue !== 'SiliconFlow Local Fixture' || !result.providerTitle.includes('SiliconFlow')) throw new Error(viewport.label + ': provider identity is incomplete');
      if (!result.modelValue.includes('DeepSeek-V4-Flash') || result.modelTitle !== result.modelValue) throw new Error(viewport.label + ': full model identity is unavailable');
      const minimumInset = viewport.width >= 1100 ? 70 : viewport.width >= 700 ? 24 : 14;
      if (result.conversationInset < minimumInset) throw new Error(viewport.label + ': conversation is not inset like a focused workbench');
      if (result.composerHeight > 112 || result.composerTextHeight > 62) throw new Error(viewport.label + ': composer remains visually oversized');
      if (result.newChatHeight > 36 || result.newChatBorderWidth > 1 || result.newChatOutlineWidth > 1) throw new Error(viewport.label + ': new chat control has an oversized card or focus ring');
      results.push({ label: viewport.label, ...result });
      await page.screenshot({ path: path.join(outputDir, `ui-responsive-${viewport.label}.png`) });
    }
    await page.setViewportSize({ width: 1280, height: 780 });
    await page.evaluate(() => { document.documentElement.dataset.theme = 'light'; });
    await page.waitForTimeout(80);
    const lightTheme = await page.evaluate(() => {
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      const color = (selector, property) => { context.clearRect(0, 0, 1, 1); context.fillStyle = getComputedStyle(document.querySelector(selector))[property];
        context.fillRect(0, 0, 1, 1); return Array.from(context.getImageData(0, 0, 1, 1).data.slice(0, 3)); };
      const luminance = values => values.reduce((sum, value) => sum + value, 0) / values.length;
      const composer = document.querySelector('.composer');
      const mainLuminance = luminance(color('.main', 'backgroundColor'));
      return { sidebarLuminance: luminance(color('.sidebar', 'backgroundColor')), topbarLuminance: luminance(color('.topbar', 'backgroundColor')),
        composerLuminance: luminance(color('.composer', 'backgroundColor')), starterContrast: Math.abs(mainLuminance - luminance(color('.starter-grid span', 'color'))), overflow: document.documentElement.scrollWidth - innerWidth,
        composerHeight: composer?.getBoundingClientRect().height || 0 };
    });
    if (lightTheme.sidebarLuminance < 190 || lightTheme.topbarLuminance < 190 || lightTheme.composerLuminance < 180 || lightTheme.starterContrast < 100 || lightTheme.overflow > 1 || lightTheme.composerHeight > 112) throw new Error('Light theme retains a dark, low-contrast or oversized primary surface: ' + JSON.stringify(lightTheme));
    await page.screenshot({ path: path.join(outputDir, 'ui-responsive-light.png') });
    await page.evaluate(() => { document.documentElement.dataset.theme = 'dark'; });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const reducedMotion = await page.evaluate(() => getComputedStyle(document.querySelector('.conversation')).scrollBehavior);
    if (reducedMotion !== 'auto') throw new Error('Reduced motion does not disable smooth conversation scrolling');
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.setViewportSize({ width: 826, height: 720 });
    await page.locator('.sidebar-actions button').filter({hasText:'开发工作台'}).click();
    await page.locator('.tree-list button').first().waitFor({state:'visible'});
    const coldFileControls=await page.evaluate(()=>{const numbers=(selector,property)=>{const value=getComputedStyle(document.querySelector(selector))[property];return(value.match(/\d+(?:\.\d+)?/g)||[]).slice(0,3).map(Number);};return{edition:document.documentElement.dataset.edition,skin:document.documentElement.dataset.skin,selectorBackground:numbers('.provider-selector select','backgroundColor'),selectorBorder:numbers('.provider-selector select','borderTopColor'),treeText:numbers('.tree-list button','color'),treeGlyph:numbers('.tree-list i','color')};});
    const isBlue=values=>values.length===3&&values[2]>values[0]&&values[2]>values[1];
    if(!isBlue(coldFileControls.selectorBackground)||!isBlue(coldFileControls.selectorBorder)||!isBlue(coldFileControls.treeText)||!isBlue(coldFileControls.treeGlyph))throw new Error('Peripheral selectors or file controls retain a warm color: '+JSON.stringify(coldFileControls));
    await page.screenshot({path:path.join(outputDir,'ui-cold-file-controls.png')});
    await page.locator('.inspector>header nav button').filter({hasText:'运行'}).click();
    await page.locator('.run-card').waitFor({state:'visible'});
    const runCenter=await page.evaluate(()=>{const pane=document.querySelector('.runs-pane'),card=document.querySelector('.run-card'),trigger=document.querySelector('.run-center-trigger');return{paneWidth:pane?.getBoundingClientRect().width||0,paneOverflow:pane?pane.scrollWidth-pane.clientWidth:999,cardWidth:card?.getBoundingClientRect().width||0,triggerText:trigger?.textContent?.trim()||'',cardText:card?.textContent?.trim()||''};});
    if(runCenter.paneWidth<300||runCenter.cardWidth<260||runCenter.paneOverflow>1||!runCenter.triggerText.includes('1')||!runCenter.cardText.includes('等待授权')||!runCenter.cardText.includes('处理授权'))throw new Error('Run Center responsive layout or approval state is invalid');
    await page.screenshot({ path: path.join(outputDir, 'ui-responsive-run-center.png') });
    const approvalLayout=await page.evaluate(()=>{const card=document.querySelector('.approval-card'),header=card?.querySelector('header');return{width:card?.getBoundingClientRect().width||0,overflow:card?card.scrollWidth-card.clientWidth:999,headerText:header?.textContent?.trim()||'',buttonText:header?.querySelector('button')?.textContent?.trim()||''};});
    if(approvalLayout.width<300||approvalLayout.overflow>1||!approvalLayout.headerText.includes('fixture-unopened-session'.slice(0,8))||approvalLayout.buttonText!=='打开会话')throw new Error('Approval ownership card is not usable');
    await page.screenshot({path:path.join(outputDir,'ui-approval-ownership.png')});
    await page.locator('.inspector>header nav button').filter({hasText:'记忆'}).click();
    await page.locator('.memory-list article').waitFor({state:'visible'});
    const memoryLayout=await page.evaluate(()=>{const pane=document.querySelector('.memory-pane'),card=document.querySelector('.memory-list article');return{paneWidth:pane?.getBoundingClientRect().width||0,paneOverflow:pane?pane.scrollWidth-pane.clientWidth:999,cardWidth:card?.getBoundingClientRect().width||0,text:card?.textContent?.trim()||'',policy:document.querySelector('.memory-policy')?.textContent?.trim()||''};});
    if(memoryLayout.paneWidth<300||memoryLayout.cardWidth<260||memoryLayout.paneOverflow>1||!memoryLayout.text.includes('默认语言与输出习惯')||!memoryLayout.text.includes('已注入')||!memoryLayout.policy.includes('下一轮 system prompt'))throw new Error('Workspace memory panel is incomplete or clips at high DPI: '+JSON.stringify(memoryLayout));
    await page.screenshot({path:path.join(outputDir,'ui-workspace-memory-dark.png')});
    await page.locator('.memory-pane .pane-toolbar button').click();
    await page.locator('.memory-editor textarea').waitFor({state:'visible'});
    const memoryEditor=await page.evaluate(()=>{const editor=document.querySelector('.memory-editor'),textarea=editor?.querySelector('textarea');return{width:editor?.getBoundingClientRect().width||0,overflow:editor?editor.scrollWidth-editor.clientWidth:999,textareaWidth:textarea?.getBoundingClientRect().width||0};});
    if(memoryEditor.width<300||memoryEditor.textareaWidth<260||memoryEditor.overflow>1)throw new Error('Workspace memory editor is not responsive: '+JSON.stringify(memoryEditor));
    await page.screenshot({path:path.join(outputDir,'ui-workspace-memory-editor-dark.png')});
    await page.evaluate(() => { document.documentElement.dataset.theme = 'light'; });
    const memoryLight=await page.evaluate(()=>{const canvas=document.createElement('canvas');canvas.width=canvas.height=1;const context=canvas.getContext('2d',{willReadFrequently:true});const luminance=selector=>{const element=document.querySelector(selector);if(!element)return-1;context.clearRect(0,0,1,1);context.fillStyle=getComputedStyle(element).backgroundColor;context.fillRect(0,0,1,1);const values=context.getImageData(0,0,1,1).data;return(values[0]+values[1]+values[2])/3;};return{pane:luminance('.main'),card:luminance('.memory-editor'),overflow:document.documentElement.scrollWidth-innerWidth};});
    if(memoryLight.pane<180||memoryLight.card<180||memoryLight.overflow>1)throw new Error('Workspace memory light theme retains a dark or overflowing surface: '+JSON.stringify(memoryLight));
    await page.screenshot({path:path.join(outputDir,'ui-workspace-memory-light.png')});
    await page.evaluate(() => { document.documentElement.dataset.theme = 'dark'; });
    await page.locator('.inspector-close').click({force:true});
    await page.locator('.sidebar-actions button').filter({ hasText: '设置' }).click();
    await page.locator('.settings-nav button').filter({ hasText: '工作区' }).click();
    const limitInput = page.locator('.agent-runtime-limits input');
    await limitInput.waitFor({ state: 'visible' });
    const runtimeLimits = await page.evaluate(() => {
      const panel = document.querySelector('.agent-runtime-limits');
      const input = panel?.querySelector('input');
      return { value: Number(input?.value || 0), panelWidth: panel?.getBoundingClientRect().width || 0,
        overflow: panel ? panel.scrollWidth - panel.clientWidth : 999 };
    });
    if (runtimeLimits.value !== 100 || runtimeLimits.panelWidth < 300 || runtimeLimits.overflow > 1) throw new Error('Agent runtime limits control is invalid');
    const startupLayout=await page.evaluate(()=>{const panel=document.querySelector('.startup-behavior'),input=panel?.querySelector('input'),toggle=panel?.querySelector('.switch span');return{width:panel?.getBoundingClientRect().width||0,overflow:panel?panel.scrollWidth-panel.clientWidth:999,text:panel?.textContent?.trim()||'',checked:!!input?.checked,toggleWidth:toggle?.getBoundingClientRect().width||0};});
    if(startupLayout.width<300||startupLayout.overflow>1||!startupLayout.text.includes('登录后保持 Agent Host')||startupLayout.checked||startupLayout.toggleWidth<32)throw new Error('Host-only login startup control is invalid: '+JSON.stringify(startupLayout));
    const settingsOptionColors=await page.evaluate(()=>{
      const rgb=()=>getComputedStyle(document.querySelector('.startup-behavior')).backgroundColor.match(/\d+(?:\.\d+)?/g).slice(0,3).map(Number);
      const edition=document.documentElement.dataset.edition;
      document.documentElement.dataset.skin='fusion';document.documentElement.dataset.theme='dark';const fusionDark=rgb();
      document.documentElement.dataset.theme='light';const fusionLight=rgb();
      document.documentElement.dataset.skin='claude';const claude=rgb();
      document.documentElement.dataset.skin='fusion';document.documentElement.dataset.theme='dark';
      return{edition,fusionDark,fusionLight,claude};
    });
    const blue=values=>values[2]>values[0]&&values[2]>values[1],brown=values=>values[0]>values[2]&&values[1]>values[2];
    if(!blue(settingsOptionColors.fusionDark)||!blue(settingsOptionColors.fusionLight)||(settingsOptionColors.edition==='local'?!brown(settingsOptionColors.claude):!blue(settingsOptionColors.claude)))throw new Error('Settings option theme colors violate the Open/Codex/mixed blue and local-Claude brown contract: '+JSON.stringify(settingsOptionColors));
    await page.evaluate(()=>document.querySelector('.startup-behavior input')?.click());
    await page.waitForFunction(()=>document.querySelector('.startup-behavior input')?.checked===true);
    const startupEnabledResponse=await request.get(baseUrl+'/api/workbench/startup'),startupEnabled=await startupEnabledResponse.json();
    if(!startupEnabledResponse.ok()||!startupEnabled.enabled||!startupEnabled.registered||startupEnabled.launchMode!=='host-only')throw new Error('Settings startup toggle did not persist the exact host-only registration');
    await page.evaluate(()=>{const approvals=document.querySelector('.approval-stack');if(approvals)approvals.style.visibility='hidden';});
    await page.screenshot({ path: path.join(outputDir, 'ui-responsive-startup-setting.png') });
    await page.evaluate(()=>document.querySelector('.startup-behavior input')?.click());
    await page.waitForFunction(()=>document.querySelector('.startup-behavior input')?.checked===false);
    await page.locator('.settings-nav button').filter({hasText:'API 设置'}).click();
    await page.locator('.cap-icon').first().waitFor({state:'visible'});
    const apiControlColors=await page.evaluate(()=>{const color=(selector,property)=>{const value=getComputedStyle(document.querySelector(selector))[property];return(value.match(/\d+(?:\.\d+)?/g)||[]).slice(0,3).map(Number);};const read=()=>({inputBackground:color('.api-settings-pane input','backgroundColor'),inputBorder:color('.api-settings-pane input','borderTopColor'),iconBackground:color('.cap-icon','backgroundColor'),iconBorder:color('.cap-icon','borderTopColor'),iconText:color('.cap-icon','color'),heading:color('.capability-head h3','color')});const edition=document.documentElement.dataset.edition;document.documentElement.dataset.skin='fusion';const fusion=read();document.documentElement.dataset.skin='claude';const claude=read();document.documentElement.dataset.skin='fusion';return{edition,fusion,claude};});
    const coldControl=value=>value.length===3&&value[2]>value[0]&&value[2]>value[1],warmControl=value=>value.length===3&&value[0]>value[2]&&value[1]>value[2];
    const allCold=value=>Object.values(value).every(coldControl),allWarm=value=>warmControl(value.inputBackground)&&warmControl(value.inputBorder)&&warmControl(value.iconBackground)&&warmControl(value.iconBorder);
    if(!allCold(apiControlColors.fusion)||(apiControlColors.edition==='local'?!allWarm(apiControlColors.claude):!allCold(apiControlColors.claude)))throw new Error('API input, frame, icon or text colors violate the cold-control contract: '+JSON.stringify(apiControlColors));
    await page.screenshot({path:path.join(outputDir,'ui-api-control-colors.png')});
    const dialogAccessibility=await page.evaluate(()=>{const dialog=document.querySelector('.provider-modal');return{role:dialog?.getAttribute('role')||'',modal:dialog?.getAttribute('aria-modal')||'',labelledBy:dialog?.getAttribute('aria-labelledby')||'',focusInside:!!dialog?.contains(document.activeElement),focusTag:document.activeElement?.tagName||''};});
    if(dialogAccessibility.role!=='dialog'||dialogAccessibility.modal!=='true'||!dialogAccessibility.labelledBy||!dialogAccessibility.focusInside)throw new Error('Settings dialog semantics or initial focus are incomplete: '+JSON.stringify(dialogAccessibility));
    const focusBoundary=await page.evaluate(()=>{const root=document.querySelector('.provider-modal'),items=[...root.querySelectorAll('button,input,select,textarea,a[href],[tabindex]:not([tabindex="-1"])')].filter(item=>!item.disabled&&item.getClientRects().length);items.at(-1)?.focus();return{first:items[0]?.outerHTML.slice(0,100)||'',last:items.at(-1)?.outerHTML.slice(0,100)||''};});
    await page.keyboard.press('Tab');
    const focusWrapped=await page.evaluate(()=>{const root=document.querySelector('.provider-modal'),items=[...root.querySelectorAll('button,input,select,textarea,a[href],[tabindex]:not([tabindex="-1"])')].filter(item=>!item.disabled&&item.getClientRects().length);return document.activeElement===items[0];});
    if(!focusWrapped)throw new Error('Settings dialog focus escaped instead of wrapping: '+JSON.stringify(focusBoundary));
    await page.screenshot({ path: path.join(outputDir, 'ui-responsive-runtime-limits.png') });
    await page.keyboard.press('Escape');
    await page.locator('.provider-modal').waitFor({state:'detached'});
    const settingsFocusReturned=await page.evaluate(()=>document.activeElement?.textContent?.includes('设置')===true);
    if(!settingsFocusReturned)throw new Error('Closing Settings did not return focus to its trigger');
    await page.keyboard.press('Control+K');
    await page.locator('.command-palette').waitFor({state:'visible'});
    const paletteAccessibility=await page.evaluate(()=>{const dialog=document.querySelector('.command-palette');return{role:dialog?.getAttribute('role')||'',modal:dialog?.getAttribute('aria-modal')||'',focusInput:document.activeElement===dialog?.querySelector('input')};});
    if(paletteAccessibility.role!=='dialog'||paletteAccessibility.modal!=='true'||!paletteAccessibility.focusInput)throw new Error('Command palette semantics or focus are incomplete: '+JSON.stringify(paletteAccessibility));
    await page.keyboard.press('Escape');
    await page.locator('.command-palette').waitFor({state:'detached'});
    const paletteFocusReturned=await page.evaluate(()=>document.activeElement?.textContent?.includes('设置')===true);
    if(!paletteFocusReturned)throw new Error('Closing command palette did not return focus to its trigger');

    const raceNow=new Date(),raceSessions=[
      {id:'race-a',claudeSessionId:'race-a',title:'Race A',workspace,createdAt:new Date(raceNow-2000).toISOString(),updatedAt:new Date(raceNow-1000).toISOString(),started:false,allowedDirs:[],queue:[]},
      {id:'race-b',claudeSessionId:'race-b',title:'Race B',workspace,createdAt:new Date(raceNow-1000).toISOString(),updatedAt:raceNow.toISOString(),started:false,allowedDirs:[],queue:[]}
    ];
    await request.post(baseUrl+'/api/sessions',{data:raceSessions});
    const raceContext=await browser.newContext({viewport:{width:826,height:720},extraHTTPHeaders:headers});
    const racePage=await raceContext.newPage();
    await racePage.route('**/api/chat/runs',route=>route.fulfill({status:200,contentType:'application/json',body:JSON.stringify([{id:'fixture-race-a',kind:'chat',sessionId:'race-a',workspace,state:'running',isActive:true,eventCursor:0,elapsedMs:1000,model:'offline'}])}));
    await racePage.route('**/api/workbench/activity?*',route=>route.abort('failed'));
    await racePage.route('**/api/sessions/race-a/messages',async route=>{await new Promise(resolve=>setTimeout(resolve,900));await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify([{id:'race-a-user',role:'user',text:'race-a-text',time:raceNow.toISOString()}])});});
    await racePage.route('**/api/sessions/race-b/messages',async route=>{if(route.request().method()==='POST'){await new Promise(resolve=>setTimeout(resolve,500));await route.fulfill({status:200,contentType:'application/json',body:'{"ok":true}'});return;}await new Promise(resolve=>setTimeout(resolve,40));await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify([{id:'race-b-user',role:'user',text:'race-b-text',time:raceNow.toISOString()},{id:'race-b-stream',role:'assistant',text:'',streaming:true,runId:'fixture-current-b',eventCursor:0,workflow:[],time:raceNow.toISOString()}])});});
    await racePage.route('**/api/chat/poll/fixture-current-b?*',route=>route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({events:[],nextSeq:0,status:{state:'running'},jobState:{}})}));
    await racePage.goto(baseUrl,{waitUntil:'domcontentloaded'});await racePage.getByText('race-b-text',{exact:true}).waitFor({state:'visible'});
    await racePage.locator('.session-entry').filter({hasText:'Race A'}).locator('.session-item').click();await racePage.waitForTimeout(30);await racePage.locator('.session-entry').filter({hasText:'Race B'}).locator('.session-item').click();await racePage.waitForTimeout(1500);
    const raceResult=await racePage.evaluate(()=>({activeTitle:document.querySelector('.session-entry.active .session-title')?.textContent?.trim()||'',conversation:document.querySelector('.conversation')?.textContent||'',switching:document.querySelector('.conversation')?.getAttribute('aria-busy')||''}));
    if(raceResult.activeTitle!=='Race B'||!raceResult.conversation.includes('race-b-text')||raceResult.conversation.includes('race-a-text'))throw new Error('Rapid session switching mixed or replaced conversation messages');
    const userMessage=racePage.locator('.message.user').filter({hasText:'race-b-text'});
    if(await userMessage.locator('.message-delete').count()||await userMessage.getByText('引用提问',{exact:true}).count())throw new Error('Message still exposes the removed red delete or footer quote control');
    await userMessage.locator('.message-body').click({button:'right'});
    const deleteOnly=await racePage.locator('.selection-menu button').allTextContents();
    if(JSON.stringify(deleteOnly)!==JSON.stringify(['删除消息']))throw new Error('Right-click without a selection must expose delete only: '+JSON.stringify(deleteOnly));
    await racePage.keyboard.press('Escape');
    await userMessage.locator('.message-body').evaluate(element=>{const node=element.querySelector('span')?.firstChild;if(!node)return;const range=document.createRange();range.selectNodeContents(node);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);element.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:300,clientY:300}));});
    await racePage.locator('.selection-menu').waitFor({state:'visible'});
    const selectionActions=await racePage.locator('.selection-menu button').allTextContents();
    if(JSON.stringify(selectionActions)!==JSON.stringify(['复制','引用','导出 TXT','删除消息']))throw new Error('Selected-text context actions are incomplete: '+JSON.stringify(selectionActions));
    await racePage.keyboard.press('Escape');
    await racePage.screenshot({path:path.join(outputDir,'ui-session-switch-race.png')});await raceContext.close();

    const pollingPaths=['/api/workbench/activity','/api/chat/runs','/api/permissions/pending','/api/task-queue'];
    const observePolling=async(page,duration)=>{const counts=Object.fromEntries(pollingPaths.map(value=>[value,0]));page.on('request',request=>{const url=new URL(request.url());if(Object.prototype.hasOwnProperty.call(counts,url.pathname))counts[url.pathname]++;});await page.goto(baseUrl,{waitUntil:'domcontentloaded'});await page.locator('.provider-selector select').waitFor({state:'visible'});await page.waitForTimeout(duration);return counts;};
    const idleContext=await browser.newContext({viewport:{width:1280,height:780},extraHTTPHeaders:headers}),idlePage=await idleContext.newPage();
    const idlePolling=await observePolling(idlePage,6500);await idleContext.close();
    if(idlePolling['/api/workbench/activity']<1||idlePolling['/api/workbench/activity']>3||idlePolling['/api/chat/runs']>2||idlePolling['/api/permissions/pending']>1||idlePolling['/api/task-queue']>2)throw new Error('Idle UI did not converge on one Host activity channel: '+JSON.stringify(idlePolling));
    const activeContext=await browser.newContext({viewport:{width:1280,height:780},extraHTTPHeaders:headers}),activePage=await activeContext.newPage();
    await activePage.route('**/api/workbench/activity?*',route=>route.abort('failed'));
    await activePage.route('**/api/chat/runs',route=>route.fulfill({status:200,contentType:'application/json',body:JSON.stringify([{id:'polling-active-fixture',kind:'chat',sessionId:'unopened-polling-session',workspace,state:'running',isActive:true,eventCursor:0,elapsedMs:1000,model:'offline'}])}));
    const activePolling=await observePolling(activePage,3400);await activeContext.close();
    if(activePolling['/api/workbench/activity']<3||activePolling['/api/chat/runs']<2||activePolling['/api/permissions/pending']<2||activePolling['/api/task-queue']<1)throw new Error('Legacy polling fallback did not recover after activity-channel failure: '+JSON.stringify(activePolling));
    process.stdout.write(JSON.stringify({ responsiveUi: 'PASS', results, lightTheme, reducedMotion, coldFileControls, runCenter, approvalLayout, memoryLayout, memoryEditor, memoryLight, runtimeLimits, startupLayout, settingsOptionColors, apiControlColors, startupRegistration:'PASS', dialogAccessibility:{settings:true,focusTrap:true,focusReturn:true,palette:true}, rapidSessionSwitch:'PASS',messageContextMenu:{deleteOnly,selectionActions},activitySync:{idle:idlePolling,fallback:activePolling} }, null, 2));
  } finally { await browser.close(); }
}

main().catch(error => { process.stderr.write((error && error.stack) || String(error)); process.exitCode = 1; });

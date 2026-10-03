const { chromium } = require('playwright');
const fs=require('fs'),path=require('path');
const base=process.env.CLAUDE_UI_BASE_URL,secret=process.env.CLAUDE_UI_SECRET,browserExe=process.env.CLAUDE_UI_BROWSER_EXE;
if(!base||!secret||!browserExe)throw new Error('Blockquote visual environment is incomplete');
const cases=[
  {id:'local-claude-dark',edition:'local',skin:'claude',theme:'dark',bg:'rgb(43, 35, 32)',accent:'rgb(208, 138, 104)',text:'rgb(226, 215, 209)'},
  {id:'local-claude-light',edition:'local',skin:'claude',theme:'light',bg:'rgb(247, 238, 233)',accent:'rgb(185, 99, 67)',text:'rgb(83, 66, 58)'},
  {id:'local-codex-dark',edition:'local',skin:'codex',theme:'dark',bg:'rgb(24, 35, 52)',accent:'rgb(120, 168, 255)',text:'rgb(216, 229, 247)'},
  {id:'local-codex-light',edition:'local',skin:'codex',theme:'light',bg:'rgb(237, 244, 255)',accent:'rgb(82, 125, 190)',text:'rgb(50, 68, 93)'},
  {id:'local-compatible-dark',edition:'local',skin:'fusion',theme:'dark',bg:'rgb(32, 39, 45)',accent:'rgb(142, 161, 185)',text:'rgb(214, 222, 231)'},
  {id:'local-compatible-light',edition:'local',skin:'fusion',theme:'light',bg:'rgb(239, 243, 246)',accent:'rgb(102, 123, 146)',text:'rgb(57, 70, 83)'},
  {id:'opensource-dark',edition:'opensource',skin:'open',theme:'dark',bg:'rgb(23, 35, 51)',accent:'rgb(115, 166, 232)',text:'rgb(217, 231, 247)'},
  {id:'opensource-light',edition:'opensource',skin:'open',theme:'light',bg:'rgb(238, 244, 250)',accent:'rgb(79, 127, 171)',text:'rgb(51, 72, 92)'}
];
(async()=>{
  const browser=await chromium.launch({headless:true,executablePath:browserExe});
  try{
    const context=await browser.newContext({viewport:{width:1100,height:650},extraHTTPHeaders:{'X-Desktop-Secret':secret,'X-Workbench-Protocol':'2'}});
    const page=await context.newPage();await page.goto(base,{waitUntil:'domcontentloaded'});await page.waitForFunction(()=>document.documentElement.dataset.interfaceReady==='true');
    await page.evaluate(()=>{const root=document.querySelector('#app'),vm=root.__vueParentComponent?.proxy||root._vnode?.component?.proxy;vm.view='chat';vm.messages=[{id:'quote-fixture',role:'assistant',text:'## 7. 一句话理解\n\n> 链表就是“用指针当绳子，把散落在内存各处的数据节点串成一串糖葫芦”——拿到绳头，就能顺着摸到每一个。\n\n如果你想深入某一部分，我可以展开讲。',workflow:[],streaming:false,createdAt:new Date().toISOString()}];vm.activeSessionId='';});
    await page.locator('.markdown-body blockquote').waitFor();
    const out=path.join(__dirname,'artifacts','blockquote-themes');fs.mkdirSync(out,{recursive:true});const results=[];
    for(const item of cases){
      const actual=await page.evaluate(item=>{const root=document.documentElement;root.dataset.edition=item.edition;root.dataset.skin=item.skin;root.dataset.skinId=item.skin;root.dataset.theme=item.theme;root.dataset.themeMode=item.theme;const style=getComputedStyle(document.querySelector('.markdown-body blockquote'));return{background:style.backgroundColor,accent:style.borderLeftColor,text:style.color};},item);
      if(actual.background!==item.bg||actual.accent!==item.accent||actual.text!==item.text)throw new Error(item.id+' quote colors mismatch: '+JSON.stringify(actual));
      await page.locator('.conversation').screenshot({path:path.join(out,item.id+'.png')});results.push({id:item.id,...actual});
    }
    process.stdout.write(JSON.stringify({blockquoteThemeVisual:'PASS',screenshots:out,results},null,2));await context.close();
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});

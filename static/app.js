const { createApp, nextTick } = Vue;
let sessionWriteQueue = Promise.resolve();
let messageWriteQueue = Promise.resolve();
let terminalView=null,terminalFitAddon=null,terminalSearchAddon=null,terminalInputQueue=Promise.resolve();

const providerPresets = {
  siliconflow: { name:'SiliconFlow', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.siliconflow.cn/v1', imageBaseUrl:'https://api.siliconflow.cn/v1' },
  deepseek: { name:'DeepSeek', authStyle:'bearer', textProtocol:'anthropic', textBaseUrl:'https://api.deepseek.com/anthropic', imageBaseUrl:'' },
  moonshot: { name:'Moonshot / Kimi', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.moonshot.cn/v1', imageBaseUrl:'' },
  zhipu: { name:'智谱 BigModel', authStyle:'x-api-key', textProtocol:'anthropic', textBaseUrl:'https://open.bigmodel.cn/api/anthropic', imageBaseUrl:'https://open.bigmodel.cn/api/paas/v4' },
  minimax: { name:'MiniMax', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.minimaxi.com/v1', imageBaseUrl:'' },
  openai: { name:'OpenAI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.openai.com/v1', imageBaseUrl:'https://api.openai.com/v1' },
  anthropic: { name:'Anthropic', authStyle:'x-api-key', textProtocol:'anthropic', textBaseUrl:'https://api.anthropic.com', imageBaseUrl:'' },
  openrouter: { name:'OpenRouter', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://openrouter.ai/api/v1', imageBaseUrl:'' },
  together: { name:'Together AI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.together.xyz/v1', imageBaseUrl:'https://api.together.xyz/v1' },
  groq: { name:'Groq', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.groq.com/openai/v1', imageBaseUrl:'' },
  mistral: { name:'Mistral AI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.mistral.ai/v1', imageBaseUrl:'' },
  xai: { name:'xAI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.x.ai/v1', imageBaseUrl:'https://api.x.ai/v1' },
  cerebras: { name:'Cerebras', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.cerebras.ai/v1', imageBaseUrl:'' },
  sambanova: { name:'SambaNova', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.sambanova.ai/v1', imageBaseUrl:'' },
  nvidia: { name:'NVIDIA NIM', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://integrate.api.nvidia.com/v1', imageBaseUrl:'' }
};

const presetForBaseUrl = baseUrl => {
  const normalized=(baseUrl||'').toLowerCase();
  return Object.entries(providerPresets).find(([,preset])=>normalized.startsWith(preset.textBaseUrl.toLowerCase().replace('/anthropic','')))?.[0] || 'custom';
};

const visualAttachmentExtensions = new Set(['.png','.jpg','.jpeg','.webp','.gif','.bmp','.tif','.tiff','.pdf']);
const unsupportedBinaryExtensions = new Set(['.doc','.docx','.xls','.xlsx','.ppt','.pptx','.zip','.rar','.7z','.exe','.dll','.bin','.iso','.apk','.mp3','.wav','.flac','.mp4','.mov','.avi','.mkv']);
const agentDocumentExtensions = new Set(['.docx','.xlsx','.pptx']);
const visualModelPatterns = ['claude-','gpt-4o','gpt-4.1','gpt-5','gemini','vision','-vl','vl-','vlm','omni','pixtral','llava','internvl','minicpm-v','glm-4v','glm-4.5v','kimi-vl','grok-4'];

const blankProvider = () => ({
  id: '', preset: 'siliconflow', name: 'SiliconFlow', token: '', authStyle: 'bearer',
  capabilities:{schemaVersion:1,models:{},evidencePolicy:'unknown-until-probed'},
  text: { enabled: true, protocol: 'openai', baseUrl: 'https://api.siliconflow.cn/v1', modelsText: '' },
  image: { enabled: true, protocol: 'openai-images', baseUrl: 'https://api.siliconflow.cn/v1', modelsText: '' }
});

const workbenchApp=createApp({
  data() {
    return {
      providers: [], sessions: [], messages: [], activeSessionId: '', workspace: 'D:\\work\\Claude', view: 'chat', projects: [], customSkins:[],edition:{id:'local',productName:'Claude Code 中文工作台',openSource:false},
      messageWindowEnd:0,messageWindowSize:160,
      settings: { providerId: '', model: '', effort: 'max', permissionMode: 'agent', allowedTools:'Read,Glob,Grep,WebSearch,WebFetch,Skill', disallowedTools:'', workspace:'D:\\work\\Claude', theme:'system', skin:'fusion' },
      prompt: '', attachments: [], busy: false, paused:false, status: '已就绪', activeJob: '', activeEventSeq:0, processedEventIds:new Set(), pollHandle: null, polling: false, pollFailures: 0,
      streamMessage: null, streamTool: null, finalResult: null, regenerationContext: null, recoveringJob: false, steering: false,
      streamTextBuffer:'',streamFlushHandle:0,
      sessionLoadSerial: 0, lastNewAt: 0, choosingFiles: false,
      providerModal: false, settingsTab: 'api', archivePreview: null, providerForm: blankProvider(), providerError: '', discoverMessage: '', discovering: '', savingProvider: false,
      attachmentNotice: '', filePreviews: {},
      contextMenu: {show:false,x:0,y:0,text:'',role:'assistant'},
      imageForm: { model: '', size: '1024x1024', prompt: '' }, imageBusy: false, imageStatus: '等待生成', imageResult: '',
      inspector:{open:false,tab:'files'}, tree:{path:'',entries:[],loading:false}, selectedFile:null, fileContent:'', fileDirty:false,
      git:{branch:'',files:[],clean:true,diff:'',staged:false,loading:false,commitMessage:''}, extensions:{skills:[],agents:[],mcpServers:{},hooks:{},plugins:{},claudeMd:[],userRoot:'',projectRoot:'',mcpFile:'',mcpFileExists:false}, extensionStatus:'', usage:{sessions:0,input:0,output:0,total:0,byModel:[],byDay:[]}, health:{},
      transcriptQuery:'', transcriptResults:[], transcriptSearching:false, checkpoints:[], terminalId:'', terminalWorkspace:'', terminalCommand:'', terminalOutput:'', terminalRunning:false,terminalSearch:'',terminalSearchResult:{resultIndex:-1,total:0},
      terminalGrid:[[]], terminalRow:0, terminalColumn:0, terminalColumns:120, terminalRows:36,
      previewUrl:'http://127.0.0.1:3000', draftTimer:null, commandHints:[], scheduler:[], scheduleForm:{text:'',at:'',repeatMinutes:0,maxRetries:3,conflictPolicy:'queue'},
      approvals:[], approvalBusy:'',
      updateConfig:{channel:'stable',manifestUrl:'',allowUnsignedPreview:false,currentVersion:'',checking:false,message:'',release:null},
      fileSuggestions:[], fileSuggestTimer:null, palette:{open:false,query:'',index:0}, connectionState:'connecting'
    };
  },
  computed: {
    selectedProvider() { return this.providers.find(p => p.id === this.settings.providerId) || null; },
    activeSkin(){return this.customSkins.find(item=>item.id===this.settings.skin)||null;},
    isOpenSource(){return !!this.edition?.openSource;},
    productName(){return this.edition?.productName||'Claude Code 中文工作台';},
    appearanceSkins(){const imported=this.customSkins.map(item=>({...item,hint:item.description||'ZIP · PNG + CSS + JSON 皮肤包'}));if(this.isOpenSource)return [{id:'open',name:'Open Agent',hint:'无第三方品牌图像的几何 Agent 标识',builtin:true},...imported];return [{id:'fusion',name:'Claude × Codex',hint:'保留指定怪兽，采用 Codex 信息层级',builtin:true},{id:'claude',name:'Claude 原生',hint:'暖色点缀与 Claude 命名',builtin:true},{id:'codex',name:'Codex 兼容',hint:'冷色极简、Codex Agent 标识',builtin:true},...imported];},
    useGlyphAgent(){return this.isOpenSource||this.settings.skin==='codex'||(this.activeSkin?.compatibility==='codex'&&!this.activeSkin?.assets?.agent);},
    agentImage(){return this.activeSkin?.assets?.agent?this.skinAssetUrl(this.activeSkin,this.activeSkin.assets.agent):'/static/assets/mascot.png';},
    agentTitle(){return this.activeSkin?.agentTitle||this.activeSkin?.name||(this.isOpenSource?'Open Agent':this.settings.skin==='codex'?'Codex Agent':'Claude Code');},
    agentLabel(){return this.agentTitle;},
    currentSession(){return this.sessions.find(session=>session.id===this.activeSessionId)||null;},
    queuedPrompts(){return this.currentSession?.queue||[];},
    renderedMessages(){const end=Math.min(this.messages.length,this.messageWindowEnd||this.messages.length),start=Math.max(0,end-this.messageWindowSize);return this.messages.slice(start,end);},
    hasOlderMessages(){const end=Math.min(this.messages.length,this.messageWindowEnd||this.messages.length);return Math.max(0,end-this.messageWindowSize)>0;},
    hasNewerMessages(){return (this.messageWindowEnd||this.messages.length)<this.messages.length;},
    terminalSearchCount(){const query=this.terminalSearch||'';if(!query)return 0;let count=0,index=0;while((index=this.terminalOutput.indexOf(query,index))>=0){count++;index+=Math.max(1,query.length);}return count;},
    textModels() { return this.selectedProvider?.text?.enabled ? (this.selectedProvider.text.models || []) : []; },
    imageModels() { return this.selectedProvider?.image?.enabled ? (this.selectedProvider.image.models || []) : []; },
    canAttachFiles(){return !!(this.selectedProvider?.text?.enabled&&this.settings.model);},
    agentMode(){return this.settings.permissionMode==='agent'||this.settings.permissionMode==='full';},
    selectedModelCapability(){return this.selectedProvider?.capabilities?.models?.[this.settings.model]||null;},
    visualCapabilityEvidence(){if(this.selectedModelCapability?.vision===true)return 'verified';if(this.selectedModelCapability?.vision===false)return 'unsupported';const model=(this.settings.model||'').toLowerCase();return this.selectedProvider?.text?.protocol==='anthropic'&&visualModelPatterns.some(pattern=>model.includes(pattern))?'inferred':'unknown';},
    visualAttachmentsSupported(){return this.visualCapabilityEvidence==='verified'||this.visualCapabilityEvidence==='inferred';},
    attachmentCapabilityLabel(){const evidence=this.visualCapabilityEvidence==='verified'?' · 接口已验证':this.visualCapabilityEvidence==='inferred'?' · 名称推断':'';return this.agentMode?(this.visualAttachmentsSupported?'Agent 全盘 · 支持文本、Office、图片与 PDF'+evidence:'Agent 全盘 · 支持文本、代码与 Office'):this.visualAttachmentsSupported?'支持文本、图片与 PDF'+evidence:'支持文本/代码文件';},
    sortedSessions() { return this.sessions.filter(session=>!session.archived&&(!this.workspace||!session.workspace||session.workspace.toLowerCase()===this.workspace.toLowerCase())).sort((a,b) => new Date(b.updatedAt)-new Date(a.updatedAt)); },
    archivedSessions(){return this.sessions.filter(session=>session.archived).sort((a,b)=>new Date(b.archivedAt||b.updatedAt)-new Date(a.archivedAt||a.updatedAt));},
    sessionGroups() {
      const today=new Date();today.setHours(0,0,0,0);const yesterday=new Date(today);yesterday.setDate(yesterday.getDate()-1);const week=new Date(today);week.setDate(week.getDate()-7);
      const groups=[{label:'今天',items:[]},{label:'昨天',items:[]},{label:'过去 7 天',items:[]},{label:'更早',items:[]}];
      for(const session of this.sortedSessions){const time=new Date(session.updatedAt);if(time>=today)groups[0].items.push(session);else if(time>=yesterday)groups[1].items.push(session);else if(time>=week)groups[2].items.push(session);else groups[3].items.push(session);}
      return groups.filter(group=>group.items.length);
    },
    slashSuggestions(){const match=this.prompt.match(/^\/([^\s]*)$/);if(!match)return[];const builtins=['init','compact','clear','context','cost','doctor','hooks','mcp','memory','permissions','plan','review','status'];const skills=(this.extensions.skills||[]).map(item=>item.name);return [...new Set([...builtins,...skills])].filter(name=>name.toLowerCase().includes(match[1].toLowerCase())).slice(0,9);},
    paletteItems(){const query=this.palette.query.trim().toLowerCase(),actions=[{id:'new',icon:'＋',title:'新建会话',hint:'在当前工作区开始',shortcut:'Ctrl N',action:'new'},{id:'workspace',icon:'▣',title:'工作区设置',hint:this.workspace,action:'workspace'},{id:'files',icon:'⌘',title:'打开文件工作台',hint:'浏览与编辑项目文件',action:'files'},{id:'search',icon:'⌕',title:'搜索 transcript',hint:'跨项目查找历史',action:'search'},{id:'terminal',icon:'〉',title:'打开终端',hint:'当前工作区命令行',action:'terminal'},{id:'appearance',icon:'◐',title:'外观与 Agent 皮肤',hint:'主题、Claude/Codex 皮肤',action:'appearance'}],sessions=this.sortedSessions.map(session=>({id:'session:'+session.id,icon:'◇',title:session.title,hint:session.workspace||'',sessionId:session.id}));return [...actions,...sessions].filter(item=>!query||`${item.title} ${item.hint}`.toLowerCase().includes(query)).slice(0,18);}
  },
  watch:{prompt(){clearTimeout(this.draftTimer);this.draftTimer=setTimeout(()=>this.saveDraft(),250);clearTimeout(this.fileSuggestTimer);const match=this.prompt.match(/(?:^|\s)@([^\s@]{1,80})$/);if(!match){this.fileSuggestions=[];return;}this.fileSuggestTimer=setTimeout(()=>this.searchFileSuggestions(match[1]),180);}},
  async mounted() {
    await this.bootstrap();
    this.keyHandler = e => { if (e.ctrlKey && e.key.toLowerCase() === 'n') { e.preventDefault(); this.newSession(); } else if(e.ctrlKey&&e.key.toLowerCase()==='k'){e.preventDefault();this.openPalette();} };
    let resizeTimer=0,resizeFrame=0;this.resizeHandler=()=>{if(resizeFrame)return;resizeFrame=requestAnimationFrame(()=>{resizeFrame=0;document.body.classList.add('is-resizing');if(this.inspector.open&&this.inspector.tab==='terminal')this.resizeTerminal();clearTimeout(resizeTimer);resizeTimer=setTimeout(()=>document.body.classList.remove('is-resizing'),120);});};
    window.addEventListener('keydown', this.keyHandler);
    window.addEventListener('resize',this.resizeHandler,{passive:true});
    this.contextCloseHandler=()=>{this.contextMenu.show=false;};window.addEventListener('mousedown',this.contextCloseHandler);
    this.pasteHandler=e=>this.handleClipboardPaste(e);window.addEventListener('paste',this.pasteHandler);
    this.schedulerTimer=setInterval(()=>{if(this.inspector.open&&this.inspector.tab==='schedule')this.loadSchedules();},5000);
    this.queueTimer=setInterval(()=>this.syncQueue(),1200);
    this.approvalTimer=setInterval(()=>this.loadApprovals(),650);
    this.terminalTimer=setInterval(()=>this.pollTerminal(),260);
  },
  beforeUnmount(){window.removeEventListener('keydown',this.keyHandler);window.removeEventListener('resize',this.resizeHandler);window.removeEventListener('mousedown',this.contextCloseHandler);window.removeEventListener('paste',this.pasteHandler);clearTimeout(this.pollHandle);clearTimeout(this.streamFlushHandle);clearInterval(this.schedulerTimer);clearInterval(this.queueTimer);clearInterval(this.approvalTimer);clearInterval(this.terminalTimer);if(terminalView){terminalView.dispose();terminalView=null;terminalFitAddon=null;terminalSearchAddon=null;}for(const url of Object.values(this.filePreviews||{})){if(url)URL.revokeObjectURL(url);}},
  methods: {
    async api(path, options={}) {
      const headers = { ...(options.headers || {}) };
      if (options.body && typeof options.body !== 'string') { headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(options.body); }
      const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),90000);
      try{const response = await fetch(path, {...options, headers,signal:controller.signal});const data = await response.json().catch(() => ({}));if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);this.connectionState='connected';return data;}
      catch(error){this.connectionState='reconnecting';this.status='Agent Host 连接中断，正在自动重连…';if(error.name==='AbortError')throw new Error('本地后端响应超时');throw error;}finally{clearTimeout(timeout);}
    },
    async bootstrap() {
      try {
        const data = await this.api('/api/bootstrap');
        this.edition=data.edition||this.edition;document.documentElement.dataset.edition=this.edition.id||'local';document.title=this.edition.productName||'Agent 中文工作台';this.providers = data.providers || []; this.sessions = (data.sessions || []).map(session=>({...session,workspace:session.workspace||data.workspace})); this.settings = {...this.settings, ...(data.settings || {})};if(this.edition.openSource&&!this.customSkins.some(item=>item.id===this.settings.skin))this.settings.skin='open';this.workspace=this.settings.workspace||data.workspace;await this.loadSkins();this.updateConfig={...this.updateConfig,...await this.api('/api/workbench/update').catch(()=>({}))};this.applyAppearance();
        this.projects=await this.api('/api/workbench/projects').catch(()=>[]);if(!this.projects.some(project=>project.path.toLowerCase()===this.workspace.toLowerCase()))this.projects.unshift({path:this.workspace,name:this.workspace.split(/[\\/]/).pop()||this.workspace});
        await this.syncTranscripts();let legacySchedules=[];try{legacySchedules=JSON.parse(localStorage.getItem('claude-workbench-scheduler')||'[]');if(!Array.isArray(legacySchedules))legacySchedules=[];}catch(_){localStorage.removeItem('claude-workbench-scheduler');}this.scheduler=await this.api('/api/workbench/schedules').catch(()=>legacySchedules);if(!Array.isArray(this.scheduler))this.scheduler=[];if(!this.scheduler.length&&legacySchedules.length){this.scheduler=legacySchedules;await this.saveSchedules();}
        if (!this.providers.some(p => p.id === this.settings.providerId)) this.settings.providerId = this.providers[0]?.id || '';
        this.ensureModels();
        const active=(data.activeJobs||[]).find(job=>job.kind==='chat');if(active?.workspace)this.workspace=active.workspace;const target=active?.sessionId&&this.sessions.some(s=>s.id===active.sessionId)?active.sessionId:this.sortedSessions[0]?.id;
        if (target) await this.activateSession(target); else this.newSession();
        if(active){this.activeJob=active.id;this.activeEventSeq=Number(active.eventCursor||0);this.processedEventIds.clear();this.busy=true;this.recoveringJob=!!active.recovered;this.status=active.recovered?'已从后端事件日志恢复任务':'任务仍在后台运行';this.streamMessage=this.messages.find(message=>message.streaming)||null;if(!this.streamMessage){this.streamMessage={id:crypto.randomUUID(),role:'assistant',text:'',streaming:true,providerName:this.selectedProvider?.name||'API',workflow:[],time:new Date().toISOString(),recovered:!!active.recovered};this.messages.push(this.streamMessage);}this.schedulePoll(80);}
      } catch (error) { this.status = '启动失败：' + error.message; }
    },
    ensureModels() {
      if (!this.textModels.includes(this.settings.model)) this.settings.model = this.textModels[0] || '';
      if (!this.imageModels.includes(this.imageForm.model)) this.imageForm.model = this.imageModels[0] || '';
    },
    async providerChanged() { this.ensureModels(); await this.saveSettings(); },
    async modelChanged(){this.revalidateAttachments();await this.saveSettings();},
    async saveSettings() { try { await this.api('/api/settings', {method:'POST', body:this.settings}); } catch (_) {} },
    applyAppearance(){const requested=this.settings.theme||'system',dark=window.matchMedia?.('(prefers-color-scheme: dark)').matches,skin=this.activeSkin;document.documentElement.dataset.theme=requested==='system'?(dark?'dark':'light'):requested;document.documentElement.dataset.themeMode=requested;document.documentElement.dataset.skin=skin?.compatibility==='codex'?'codex':(this.settings.skin||'fusion');document.documentElement.dataset.skinId=this.settings.skin||'fusion';let link=document.getElementById('agent-skin-css');if(skin?.css){if(!link){link=document.createElement('link');link.id='agent-skin-css';link.rel='stylesheet';document.head.appendChild(link);}link.href=this.skinAssetUrl(skin,skin.css)+'?v='+encodeURIComponent(skin.id);}else if(link)link.remove();},
    async setAppearance(key,value){this.settings[key]=value;this.applyAppearance();await this.saveSettings();},
    skinAssetUrl(skin,file){return `/api/workbench/skins/asset/${encodeURIComponent(skin.id)}/${String(file||'').split('/').map(encodeURIComponent).join('/')}`;},
    async loadSkins(){this.customSkins=await this.api('/api/workbench/skins').catch(()=>[]);if(!Array.isArray(this.customSkins))this.customSkins=[];},
    async importSkin(){try{const skin=await this.api('/api/workbench/skins/import',{method:'POST',body:{}});if(skin.cancelled)return;await this.loadSkins();this.settings.skin=skin.id;this.applyAppearance();await this.saveSettings();this.status=`已导入并启用皮肤：${skin.name}`;}catch(error){this.status='导入皮肤失败：'+error.message;}},
    async deleteSkin(item){if(!item?.imported||!confirm(`删除皮肤“${item.name}”？\n\n只删除工作台保存的皮肤副本，不影响原始文件。`))return;try{await this.api('/api/workbench/skins/delete',{method:'POST',body:{id:item.id}});if(this.settings.skin===item.id)this.settings.skin='fusion';await this.loadSkins();this.applyAppearance();await this.saveSettings();this.status='皮肤已删除';}catch(error){this.status='删除皮肤失败：'+error.message;}},
    async saveUpdateConfig(){try{this.updateConfig={...this.updateConfig,...await this.api('/api/workbench/update/config',{method:'POST',body:{channel:this.updateConfig.channel,manifestUrl:this.updateConfig.manifestUrl,allowUnsignedPreview:this.updateConfig.allowUnsignedPreview}})};this.updateConfig.message='更新通道设置已保存';}catch(error){this.updateConfig.message='保存失败：'+error.message;}},
    async checkUpdate(){this.updateConfig.checking=true;this.updateConfig.message='正在读取签名发布清单…';try{const data=await this.api('/api/workbench/update/check',{method:'POST',body:{}});this.updateConfig.release=data.release||null;this.updateConfig.message=!data.configured?'尚未配置 HTTPS 发布清单':data.available?`发现 ${data.release.version}，下载前仍会校验 SHA-256 与 Authenticode`:'当前已是该通道最新版';}catch(error){this.updateConfig.message='检查失败：'+error.message;}finally{this.updateConfig.checking=false;}},
    openPalette(){this.palette.open=true;this.palette.query='';this.palette.index=0;nextTick(()=>this.$refs.paletteInput?.focus());},
    closePalette(){this.palette.open=false;},
    paletteQueryChanged(){this.palette.index=0;},
    movePalette(delta){const count=this.paletteItems.length;if(!count)return;this.palette.index=(Number(this.palette.index||0)+delta+count)%count;nextTick(()=>document.querySelector('.palette-results button.active')?.scrollIntoView({block:'nearest'}));},
    executePalette(){const item=this.paletteItems[Math.max(0,Math.min(Number(this.palette.index||0),this.paletteItems.length-1))];if(item)this.runPaletteItem(item);},
    async runPaletteItem(item){this.closePalette();if(item.sessionId)return this.activateSession(item.sessionId);if(item.action==='new')return this.newSession();if(item.action==='workspace')return this.openSettings('workspace');if(item.action==='appearance')return this.openSettings('appearance');if(['files','search','terminal'].includes(item.action))return this.openInspector(item.action);},
    formatTime(value) { if (!value) return ''; const d = new Date(value); return `${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')} ${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`; },
    sessionDisplayTitle(value){return String(value||'').includes('\uFFFD')?'旧会话（标题编码损坏）':value||'新的会话';},
    number(value) { return Number(value || 0).toLocaleString('zh-CN'); },
    useStarter(text) { this.prompt = text; },
    async syncTranscripts(){
      const summaries=await this.api(`/api/workbench/transcripts?workspace=${encodeURIComponent(this.workspace)}`).catch(()=>[]),known=new Map(this.sessions.map(session=>[session.id,session]));
      for(const item of summaries){const existing=known.get(item.id);if(existing){existing.title=item.title||existing.title;existing.updatedAt=item.updatedAt||existing.updatedAt;existing.createdAt=item.createdAt||existing.createdAt;existing.workspace=item.workspace||existing.workspace;existing.started=true;existing.transcript=true;existing.gitBranch=item.gitBranch||'';}else this.sessions.push({id:item.id,claudeSessionId:item.id,title:item.title,createdAt:item.createdAt,updatedAt:item.updatedAt,workspace:item.workspace||this.workspace,started:true,transcript:true,allowedDirs:[],queue:[]});}
    },
    async addProject(){try{const project=await this.api('/api/workbench/projects/add',{method:'POST',body:{}});if(project.cancelled)return;if(!this.projects.some(item=>item.path.toLowerCase()===project.path.toLowerCase()))this.projects.push(project);await this.switchProject(project.path);}catch(error){this.status='添加工作区失败：'+error.message;}},
    async switchProject(path){if(!path||this.busy)return;this.saveDraft();this.workspace=path;this.settings.workspace=path;await this.saveSettings();await this.syncTranscripts();const target=this.sortedSessions[0];if(target)await this.activateSession(target.id);else this.newSession();if(this.inspector.open)await this.refreshInspector();},
    async removeProject(project){if(!project||project.path.toLowerCase()===this.workspace.toLowerCase())return;try{await this.api('/api/workbench/projects/remove',{method:'POST',body:{path:project.path}});this.projects=this.projects.filter(item=>item.path.toLowerCase()!==project.path.toLowerCase());this.status=`已从列表移除 ${project.name}，磁盘文件未改动`;}catch(error){this.status='移除工作区失败：'+error.message;}},
    async searchFileSuggestions(query){try{this.fileSuggestions=await this.api(`/api/workbench/files/search?workspace=${encodeURIComponent(this.workspace)}&query=${encodeURIComponent(query)}`);}catch(_){this.fileSuggestions=[];}},
    useFileSuggestion(file){this.prompt=this.prompt.replace(/(?:^|\s)@([^\s@]{1,80})$/,match=>`${match.startsWith(' ')?' ':''}@${file.path} `);this.fileSuggestions=[];nextTick(()=>document.querySelector('.composer textarea')?.focus());},
    newSession() {
      const nowMs=Date.now();if(this.busy||nowMs-this.lastNewAt<500)return;this.lastNewAt=nowMs;
      const now = new Date().toISOString(), id=crypto.randomUUID(), session = { id, claudeSessionId:id, title:'新的会话', createdAt:now, updatedAt:now, workspace:this.workspace, started:false, allowedDirs:[], queue:[] };
      this.sessions.push(session); this.activeSessionId = session.id; this.messages = []; this.saveSessions(); nextTick(() => document.querySelector('.composer textarea')?.focus());
    },
    async activateSession(id,force=false) {
      if (this.busy || (!force&&id===this.activeSessionId)) return;this.saveDraft();this.activeEventSeq=0;this.processedEventIds.clear(); const serial=++this.sessionLoadSerial; this.activeSessionId = id;const session=this.sessions.find(item=>item.id===id);if(session?.workspace)this.workspace=session.workspace;
      try { let loaded=[];if(session?.started)loaded=(await this.api(`/api/workbench/transcripts/${session.claudeSessionId||id}?workspace=${encodeURIComponent(session.workspace||this.workspace)}`).catch(()=>null))?.messages||[];if(!loaded.length)loaded=await this.api(`/api/sessions/${id}/messages`);if(serial===this.sessionLoadSerial){this.messages=loaded;this.loadAttachmentPreviews(loaded.flatMap(message=>message.attachments||[]));this.prompt=localStorage.getItem(`claude-draft:${id}`)||'';await this.loadCheckpoints();} } catch (_) { if(serial===this.sessionLoadSerial)this.messages=[]; }
      this.scrollBottom();
    },
    saveSessions() { const snapshot=JSON.parse(JSON.stringify(this.sessions));sessionWriteQueue=sessionWriteQueue.catch(()=>{}).then(()=>this.api('/api/sessions',{method:'POST',body:snapshot}));return sessionWriteQueue; },
    saveMessages() { if(!this.activeSessionId)return Promise.resolve();const id=this.activeSessionId,snapshot=JSON.parse(JSON.stringify(this.messages));messageWriteQueue=messageWriteQueue.catch(()=>{}).then(()=>this.api(`/api/sessions/${id}/messages`,{method:'POST',body:snapshot}));return messageWriteQueue; },
    async archiveSession(){if(this.activeSessionId)await this.archiveSessionById(this.activeSessionId);},
    async archiveSessionById(id){if(this.busy||!id)return;const target=this.sessions.find(session=>session.id===id);if(!target||target.archived)return;target.archived=true;target.archivedAt=new Date().toISOString();target.updatedAt=target.archivedAt;const wasActive=id===this.activeSessionId;await this.saveSessions();if(wasActive){this.activeSessionId='';this.messages=[];const next=this.sortedSessions[0];if(next)await this.activateSession(next.id);else this.newSession();}this.status='会话已归档，可在设置中查看或恢复';},
    async viewArchivedSession(id){const session=this.sessions.find(item=>item.id===id&&item.archived);if(!session)return;try{const messages=await this.api(`/api/sessions/${id}/messages`);this.archivePreview={session,messages};this.loadAttachmentPreviews(messages.flatMap(message=>message.attachments||[]));}catch(error){this.status='读取归档失败：'+error.message;}},
    async restoreArchivedSession(id){const session=this.sessions.find(item=>item.id===id&&item.archived);if(!session)return;session.archived=false;delete session.archivedAt;session.updatedAt=new Date().toISOString();await this.saveSessions();if(this.archivePreview?.session?.id===id)this.archivePreview=null;this.status='会话已恢复到左侧列表';},
    transcriptIdsFor(session,messages=[]){const ids=new Set([session?.id,session?.claudeSessionId]);for(const message of messages||[]){if(message?.claudeSessionId)ids.add(message.claudeSessionId);for(const variant of message?.variants||[])if(variant?.claudeSessionId)ids.add(variant.claudeSessionId);}return [...ids].filter(Boolean);},
    async permanentlyDeleteArchived(id,skipConfirm=false){const session=this.sessions.find(item=>item.id===id&&item.archived);if(!session)return false;if(!skipConfirm&&!confirm(`永久删除“${session.title}”？\n\n这会同时删除 GUI 会话记录和 Claude Code transcript，无法恢复。`))return false;try{const messages=await this.api(`/api/sessions/${id}/messages`).catch(()=>[]);await this.api(`/api/sessions/${id}`,{method:'DELETE',body:{workspace:session.workspace||this.workspace,transcriptIds:this.transcriptIdsFor(session,messages)}});this.sessions=this.sessions.filter(item=>item.id!==id);if(this.archivePreview?.session?.id===id)this.archivePreview=null;await this.saveSessions();this.status='会话及 transcript 已永久删除';return true;}catch(error){this.status='永久删除失败：'+error.message;return false;}},
    async clearArchivedSessions(){const archived=[...this.archivedSessions];if(!archived.length||!confirm(`永久清空 ${archived.length} 个归档会话？\n\n所有对应 GUI 记录和 Claude Code transcript 都会被删除，无法恢复。`))return;let deleted=0;for(const session of archived)if(await this.permanentlyDeleteArchived(session.id,true))deleted++;this.status=`已永久删除 ${deleted}/${archived.length} 个归档会话`;},
    async deleteMessage(id){if(this.busy||!id)return;const message=this.messages.find(item=>item.id===id);if(!message||!confirm(`删除这条${message.role==='user'?'用户':'助手'}消息？此操作无法撤销。`))return;this.messages=this.messages.filter(item=>item.id!==id);const session=this.sessions.find(item=>item.id===this.activeSessionId);if(session){const firstUser=this.messages.find(item=>item.role==='user'&&item.text?.trim());session.title=firstUser?(firstUser.text.length>25?firstUser.text.slice(0,25)+'…':firstUser.text):'新的会话';session.updatedAt=new Date().toISOString();}await Promise.all([this.saveMessages(),this.saveSessions()]);this.status='消息已删除';},
    selectedTextWithinMessage(event){const selection=window.getSelection?.(),article=event?.currentTarget?.closest?.('.message');if(selection&&!selection.isCollapsed&&article&&article.contains(selection.anchorNode)&&article.contains(selection.focusNode))return selection.toString().trim();return '';},
    selectedMessageText(message,event){return this.selectedTextWithinMessage(event)||message?.text||'';},
    async copyMessage(message,event){const text=this.selectedMessageText(message,event);if(!text)return;try{await navigator.clipboard.writeText(text);this.status=text===message.text?'已复制整条消息':'已复制所选内容';}catch(_){const box=document.createElement('textarea');box.value=text;box.style.position='fixed';box.style.opacity='0';document.body.appendChild(box);box.select();document.execCommand('copy');box.remove();this.status='已复制';}},
    appendQuote(text,whole=false,role='assistant'){if(!text)return;const quoted=text.split(/\r?\n/).map(line=>`> ${line}`).join('\n'),source=role==='user'?'我的消息':role==='system'?'系统消息':'Agent 回复',block=`引用${source}：\n${quoted}\n\n`;this.prompt=this.prompt?`${this.prompt.trimEnd()}\n\n${block}`:block;this.status=whole?`已引用整条${source}`:`已引用所选${source}`;nextTick(()=>{const box=document.querySelector('.composer textarea');if(box){box.focus();box.selectionStart=box.selectionEnd=box.value.length;}});},
    quoteMessage(message,event){const selected=this.selectedTextWithinMessage(event),text=selected||message?.text||'';this.appendQuote(text,!selected,message?.role||'assistant');},
    openSelectionMenu(message,event){this.contextMenu.show=false;const selected=this.selectedTextWithinMessage(event);if(!selected)return;event.preventDefault();const width=216,height=38;this.contextMenu={show:true,x:Math.min(event.clientX,window.innerWidth-width-8),y:Math.min(event.clientY,window.innerHeight-height-8),text:selected,role:message?.role||'assistant'};},
    async copyContextSelection(){const text=this.contextMenu.text;this.contextMenu.show=false;if(!text)return;try{await navigator.clipboard.writeText(text);this.status='已复制所选内容';}catch(_){const box=document.createElement('textarea');box.value=text;document.body.appendChild(box);box.select();document.execCommand('copy');box.remove();this.status='已复制';}},
    quoteContextSelection(){const {text,role}=this.contextMenu;this.contextMenu.show=false;this.appendQuote(text,false,role);},
    async exportContextSelection(){const {text,role}=this.contextMenu;this.contextMenu.show=false;if(!text)return;const label=role==='user'?'我的问题':role==='system'?'系统消息':'Agent回答',stamp=new Date().toISOString().slice(0,19).replace(/[T:]/g,'-');try{const result=await this.api('/api/files/export-text',{method:'POST',body:{fileName:`${label}-${stamp}.txt`,text}});this.status=result.saved?`已导出：${result.path}`:'已取消导出';}catch(error){this.status='导出失败：'+error.message;}},
    async exportMessage(message,event){const text=this.selectedMessageText(message,event);if(!text)return;const role=message.role==='user'?'我的问题':'Claude回答',stamp=new Date().toISOString().slice(0,19).replace(/[T:]/g,'-');try{const result=await this.api('/api/files/export-text',{method:'POST',body:{fileName:`${role}-${stamp}.txt`,text}});this.status=result.saved?`已导出：${result.path}`:'已取消导出';}catch(error){this.status='导出失败：'+error.message;}},
    async exportSessionMarkdown(){if(!this.messages.length)return;const session=this.currentSession||{title:'会话'},stamp=new Date().toISOString().slice(0,19).replace(/[T:]/g,'-'),parts=[`# ${session.title}`,'',`工作区：${session.workspace||this.workspace}`,''];for(const message of this.messages){const role=message.role==='user'?'你':message.role==='assistant'?this.agentLabel:'系统';parts.push(`## ${role}`,'',message.text||'',...(message.attachments?.length?['',`附件：${message.attachments.join('、')}`]:[]),'');}try{const result=await this.api('/api/files/export-text',{method:'POST',body:{fileName:`${session.title.replace(/[\\/:*?\"<>|]/g,'-').slice(0,45)||'会话'}-${stamp}.md`,text:parts.join('\n')}});this.status=result.saved?`已导出 Markdown：${result.path}`:'已取消导出';}catch(error){this.status='导出会话失败：'+error.message;}},
    variantSnapshot(message,claudeSessionId=''){return{id:crypto.randomUUID(),text:message.text||'',workflow:JSON.parse(JSON.stringify(message.workflow||[])),usage:message.usage?JSON.parse(JSON.stringify(message.usage)):null,providerName:message.providerName||'',time:message.time||new Date().toISOString(),claudeSessionId:claudeSessionId||message.claudeSessionId||''};},
    ensureVariants(message){if(message.variants?.length)return;const session=this.sessions.find(item=>item.id===this.activeSessionId);message.variants=[this.variantSnapshot(message,message.claudeSessionId||session?.claudeSessionId||session?.id||'')];message.activeVariant=0;},
    syncActiveVariant(message){if(!message?.variants?.length)return;const index=Number(message.activeVariant||0);const current=message.variants[index];if(!current)return;Object.assign(current,{text:message.text||'',workflow:JSON.parse(JSON.stringify(message.workflow||[])),usage:message.usage?JSON.parse(JSON.stringify(message.usage)):null,providerName:message.providerName||'',time:message.time||current.time,claudeSessionId:message.claudeSessionId||current.claudeSessionId||''});},
    applyVariant(message,index){const variant=message?.variants?.[index];if(!variant)return;message.activeVariant=index;message.text=variant.text||'';message.workflow=JSON.parse(JSON.stringify(variant.workflow||[]));message.usage=variant.usage?JSON.parse(JSON.stringify(variant.usage)):null;message.providerName=variant.providerName||message.providerName;message.time=variant.time||message.time;message.claudeSessionId=variant.claudeSessionId||'';message.streaming=false;},
    canRegenerate(message){const last=[...this.messages].reverse().find(item=>item.role==='assistant');return !this.busy&&message?.role==='assistant'&&!message.streaming&&last?.id===message.id;},
    async switchVariant(message,direction){if(this.busy||!message?.variants?.length)return;this.syncActiveVariant(message);const next=Math.max(0,Math.min(message.variants.length-1,Number(message.activeVariant||0)+direction));if(next===Number(message.activeVariant||0))return;this.applyVariant(message,next);const session=this.sessions.find(item=>item.id===this.activeSessionId),variant=message.variants[next];if(session&&variant.claudeSessionId){session.claudeSessionId=variant.claudeSessionId;session.started=true;session.updatedAt=new Date().toISOString();}await Promise.all([this.saveMessages(),this.saveSessions()]);this.status=`已选择第 ${next+1}/${message.variants.length} 个回答作为后续上下文`;},
    branchPromptFor(message){const targetIndex=this.messages.findIndex(item=>item.id===message.id),history=this.messages.slice(0,targetIndex).filter(item=>item.role==='user'||item.role==='assistant');const lines=['下面是此前对话。请把它作为上下文，但不要复述标签；重新独立回答最后一条用户要求。'];for(const item of history){lines.push(`\n[${item.role==='user'?'用户':'助手'}]\n${item.text||''}`);if(item.attachments?.length)lines.push(`附件路径：\n${item.attachments.join('\n')}`);}lines.push('\n请生成一个新的回答版本。新回答将替代此前同一轮的回答，直接给出答案。');return lines.join('\n');},
    async regenerateMessage(message){if(!this.canRegenerate(message))return;const session=this.sessions.find(item=>item.id===this.activeSessionId);if(!session)return;this.ensureVariants(message);this.syncActiveVariant(message);const previousIndex=Number(message.activeVariant||0),previousClaudeSessionId=session.claudeSessionId||session.id,newClaudeSessionId=crypto.randomUUID();const contextMessages=this.messages.slice(0,this.messages.findIndex(item=>item.id===message.id));const branchAttachments=[...new Set(contextMessages.flatMap(item=>item.attachments||[]))];const placeholder={id:crypto.randomUUID(),text:'',workflow:[],usage:null,providerName:this.selectedProvider?.name||'API',time:new Date().toISOString(),claudeSessionId:newClaudeSessionId};message.variants.push(placeholder);message.activeVariant=message.variants.length-1;this.applyVariant(message,message.activeVariant);message.streaming=true;this.streamMessage=message;this.streamTool=null;this.finalResult=null;this.regenerationContext={messageId:message.id,previousIndex,previousClaudeSessionId,newClaudeSessionId};session.claudeSessionId=newClaudeSessionId;session.started=false;session.allowedDirs=[...new Set([...(session.allowedDirs||[]),...branchAttachments.map(path=>this.attachmentDir(path)).filter(Boolean)])];this.busy=true;this.status='正在重新生成回答…';await Promise.all([this.saveMessages(),this.saveSessions()]);this.scrollBottom();try{const data=await this.api('/api/chat/start',{method:'POST',body:{workspace:session.workspace||this.workspace,prompt:this.branchPromptFor(message),attachments:branchAttachments,allowedDirs:session.allowedDirs,sessionId:session.id,claudeSessionId:newClaudeSessionId,resume:false,providerId:this.settings.providerId,model:this.settings.model,effort:this.settings.effort,permissionMode:this.settings.permissionMode,allowedTools:this.toolList(this.settings.allowedTools),disallowedTools:this.toolList(this.settings.disallowedTools)}});this.activeJob=data.jobId;this.activeEventSeq=Number(data.eventCursor||0);this.processedEventIds.clear();this.pollFailures=0;this.schedulePoll(120);}catch(error){await this.finishChat(false,error.message);}},
    async chooseFiles() {
      if(this.choosingFiles)return;this.choosingFiles=true;
      try {
        const data=await this.api('/api/files/select',{method:'POST'});const files=data.files||[];
        const known=new Set((this.attachments||[]).map(path=>path.toLowerCase()));
        const rejected=[];let added=0;
        for(const path of files){const reason=this.attachmentUnsupportedReason(path);if(reason){rejected.push(`${this.attachmentName(path)}：${reason}`);continue;}if(path&&!known.has(path.toLowerCase())){this.attachments.push(path);known.add(path.toLowerCase());added+=1;}}
        this.attachmentNotice=rejected.length?`未添加 ${rejected.length} 个文件：${rejected.slice(0,2).join('；')}${rejected.length>2?'…':''}`:'';
        if(added){this.status=`已添加 ${added} 个文件`;this.loadAttachmentPreviews(this.attachments);}else if(rejected.length)this.status='部分文件不受当前模型支持';
      } catch (error) { this.status='选择文件失败：'+error.message; }finally{this.choosingFiles=false;}
    },
    attachmentName(path){return (path||'').split(/[\\/]/).pop()||path;},
    attachmentDir(path){const normalized=(path||'').replace(/\\/g,'/'),index=normalized.lastIndexOf('/');return index>0?normalized.slice(0,index).replace(/\//g,'\\'):'';},
    attachmentExtension(path){const name=this.attachmentName(path).toLowerCase(),index=name.lastIndexOf('.');return index>0?name.slice(index):'';},
    isImageAttachment(path){return new Set(['.png','.jpg','.jpeg','.webp','.gif','.bmp']).has(this.attachmentExtension(path));},
    fileTypeClass(path){const extension=this.attachmentExtension(path);if(['.doc','.docx'].includes(extension))return 'word';if(['.xls','.xlsx','.csv'].includes(extension))return 'sheet';if(['.ppt','.pptx'].includes(extension))return 'slide';if(extension==='.pdf')return 'pdf';if(this.isImageAttachment(path))return 'image';if(['.js','.ts','.tsx','.jsx','.vue','.cs','.py','.ps1','.java','.go','.rs','.cpp','.c','.h','.html','.css','.json','.xml','.yaml','.yml','.sql'].includes(extension))return 'code';return 'file';},
    fileIcon(path){const type=this.fileTypeClass(path);return type==='word'?'W':type==='sheet'?'X':type==='slide'?'P':type==='pdf'?'PDF':type==='image'?'IMG':type==='code'?'</>':'FILE';},
    fileKind(path){const extension=this.attachmentExtension(path);const labels={'.doc':'Word','.docx':'Word','.xls':'Excel','.xlsx':'Excel','.csv':'CSV','.ppt':'PowerPoint','.pptx':'PowerPoint','.pdf':'PDF','.txt':'文本','.md':'Markdown','.json':'JSON','.png':'图片','.jpg':'图片','.jpeg':'图片','.webp':'图片','.gif':'图片','.bmp':'图片'};return labels[extension]||(extension?extension.slice(1).toUpperCase():'本地文件');},
    filePathHint(path){const dir=this.attachmentDir(path),parts=dir.split(/[\\/]/).filter(Boolean);return parts.length?`${parts[parts.length-1]} · 本地引用`:'本地引用';},
    async loadFilePreview(path){if(!path||!this.isImageAttachment(path)||this.filePreviews[path])return;try{const response=await fetch(`/api/files/preview?path=${encodeURIComponent(path)}`);if(!response.ok)return;const blob=await response.blob();this.filePreviews={...this.filePreviews,[path]:URL.createObjectURL(blob)};}catch(_){ }},
    loadAttachmentPreviews(paths){for(const path of new Set(paths||[]))this.loadFilePreview(path);},
    async openLocalFile(path){try{await this.api('/api/files/open',{method:'POST',body:{path}});}catch(error){this.status='打开文件失败：'+error.message;}},
    attachmentUnsupportedReason(path){const extension=this.attachmentExtension(path);if(this.agentMode&&agentDocumentExtensions.has(extension))return '';if(unsupportedBinaryExtensions.has(extension))return extension==='.doc'||extension==='.docx'?'DOC/DOCX 不能由 Read 直接读取；请切换到 Agent（全盘），或另存为 TXT/Markdown':'该二进制格式不能由当前 Read 工具直接读取';if(visualAttachmentExtensions.has(extension)&&!this.visualAttachmentsSupported)return this.selectedProvider?.text?.protocol==='openai'?'当前 OpenAI 适配器仅传递文本内容，不支持图片/PDF':'当前模型未识别出视觉能力，请切换到 Vision/VL/Claude 多模态模型';return '';},
    revalidateAttachments(){const rejected=(this.attachments||[]).filter(path=>this.attachmentUnsupportedReason(path));if(!rejected.length)return;this.attachments=this.attachments.filter(path=>!rejected.includes(path));this.attachmentNotice=`模型切换后已移除 ${rejected.length} 个不支持的附件：${rejected.map(path=>this.attachmentName(path)).slice(0,3).join('、')}`;this.status='已移除当前模型不支持的附件';},
    removeAttachment(path){if(this.busy)return;this.attachments=this.attachments.filter(item=>item!==path);this.attachmentNotice='';this.status=this.attachments.length?`待发送 ${this.attachments.length} 个文件`:'已取消附件';},
    clearAttachments(){if(this.busy)return;this.attachments=[];this.attachmentNotice='';this.status='已取消全部附件';},
    handleComposerKeydown(event){if(event.key!=='Enter'||event.shiftKey||event.isComposing)return;event.preventDefault();if(this.busy)this.enqueuePrompt();else this.sendPrompt();},
    queuedRequest(session,text){return {workspace:session.workspace||this.workspace,prompt:text,attachments:[],allowedDirs:session.allowedDirs||[],sessionId:session.id,claudeSessionId:session.claudeSessionId||session.id,resume:!!session.started,providerId:this.settings.providerId,model:this.settings.model,effort:this.settings.effort,permissionMode:this.settings.permissionMode,allowedTools:this.toolList(this.settings.allowedTools),disallowedTools:this.toolList(this.settings.disallowedTools)};},
    async enqueuePrompt(){const text=this.prompt.trim(),session=this.currentSession;if(!text||!session)return;try{const item=await this.api('/api/task-queue',{method:'POST',body:{sessionId:session.id,text,kind:'queued',request:this.queuedRequest(session,text)}});session.queue=session.queue||[];session.queue.push(item);session.updatedAt=new Date().toISOString();this.prompt='';await this.saveSessions();this.status=`已交给后台队列 · 共 ${session.queue.length} 条`;}catch(error){this.status='加入后台队列失败：'+error.message;}},
    async prioritizeQueued(id){const session=this.currentSession,item=session?.queue?.find(value=>value.id===id);if(!item||this.steering)return;this.steering=true;try{await this.api(`/api/task-queue/prioritize/${id}`,{method:'POST',body:{steer:true}});item.kind='steer';this.status=this.busy?'已设为优先转向，Host 正在注入当前任务':'已移到后台队列首位';await this.syncQueue();}catch(error){this.status='更改方向失败：'+error.message;}finally{this.steering=false;}},
    async removeQueued(id){const session=this.currentSession;if(!session?.queue)return;try{await this.api(`/api/task-queue/cancel/${id}`,{method:'POST'});session.queue=session.queue.filter(item=>item.id!==id);this.status='已从后台队列移除';}catch(error){this.status='移除队列指令失败：'+error.message;}},
    async runNextQueued(){await this.syncQueue(true);},
    async syncQueue(force=false){const session=this.currentSession;if(!session||this.connectionState==='disconnected')return;const queue=await this.api(`/api/task-queue?sessionId=${encodeURIComponent(session.id)}`).catch(()=>null);if(!Array.isArray(queue))return;session.queue=queue.filter(item=>item.state==='queued');const running=queue.find(item=>item.state==='running'||item.state==='starting');if(!this.busy&&running?.runId){const boot=await this.api('/api/bootstrap').catch(()=>null),active=boot?.activeJobs?.find(item=>item.id===running.runId);if(active){this.activeJob=active.id;this.activeEventSeq=Number(active.eventCursor||0);this.processedEventIds.clear();this.busy=true;this.recoveringJob=true;this.streamMessage={id:crypto.randomUUID(),role:'assistant',text:'',streaming:true,providerName:this.selectedProvider?.name||'API',workflow:[],time:new Date().toISOString(),recovered:true};this.messages.push(this.streamMessage);this.status='后台队列任务已开始';this.schedulePoll(80);}}if(force)await this.saveSessions();},
    async sendPrompt(options={}) {
      const typedText = String(options.textOverride??this.prompt).trim(); if ((!typedText && !this.attachments.length) || this.busy) return;
      const text=typedText||'请读取并分析我附加的文件，说明其中的关键信息。';
      const requestText=options.queueKind==='steer'?`方向调整：下面是当前最高优先级的新要求。请据此调整后续工作方向，同时保留仍然适用的已完成结果。\n\n${text}`:text;
      if (!this.selectedProvider?.text?.enabled) return alert('当前 API 没有启用 Text capability，请先导入或编辑 API 配置。');
      if (!this.settings.model) return alert('当前 API 没有可用的文字模型。');
      let session = this.sessions.find(s => s.id === this.activeSessionId); if (!session) { this.newSession(); session = this.sessions.find(s => s.id === this.activeSessionId); }
      const pendingAttachments=options.textOverride==null?[...this.attachments]:[];
      session.allowedDirs=[...new Set([...(session.allowedDirs||[]),...pendingAttachments.map(path=>this.attachmentDir(path)).filter(Boolean)])];
      const userMessage = {id:crypto.randomUUID(), role:'user', text, attachments:pendingAttachments, queueKind:options.queueKind||'', time:new Date().toISOString()};
      this.messages.push(userMessage); this.streamMessage = {id:crypto.randomUUID(), role:'assistant', text:'', streaming:true, providerName:this.selectedProvider.name, workflow:[], time:new Date().toISOString()}; this.messages.push(this.streamMessage);
      if (session.title === '新的会话') session.title = text.length > 25 ? text.slice(0,25)+'…' : text; session.updatedAt = new Date().toISOString();
      if(options.textOverride==null){this.prompt='';this.attachments=[];this.attachmentNotice='';} this.busy = true; this.status = '正在启动 Claude Code…'; this.finalResult = null; this.streamTool = null; await Promise.all([this.saveSessions(),this.saveMessages()]); this.scrollBottom();
      try {
        const data = await this.api('/api/chat/start', {method:'POST', body:{workspace:session.workspace||this.workspace,prompt:requestText, attachments:pendingAttachments, allowedDirs:session.allowedDirs, sessionId:session.id, claudeSessionId:session.claudeSessionId||session.id, resume:!!session.started, providerId:this.settings.providerId, model:this.settings.model, effort:this.settings.effort, permissionMode:this.settings.permissionMode,allowedTools:this.toolList(this.settings.allowedTools),disallowedTools:this.toolList(this.settings.disallowedTools)}});
        this.activeJob = data.jobId;this.activeEventSeq=Number(data.eventCursor||0);this.processedEventIds.clear();this.pollFailures=0;session.started=true;session.updatedAt=new Date().toISOString();await this.saveSessions();this.schedulePoll(120);
      } catch (error) { this.finishChat(false, error.message); }
    },
    schedulePoll(delay=220){clearTimeout(this.pollHandle);if(this.activeJob)this.pollHandle=setTimeout(()=>this.pollChat(),delay);},
    async pollChat() {
      if (!this.activeJob||this.polling) return;this.polling=true;
      const polledJob=this.activeJob;
      try {
        const data = await this.api(`/api/chat/poll/${polledJob}?after=${this.activeEventSeq}`);
        if(this.activeJob!==polledJob)return;
        this.pollFailures=0;
        if(Array.isArray(data.events)){for(const event of data.events){if(!event?.id||this.processedEventIds.has(event.id)||Number(event.seq)<=this.activeEventSeq)continue;this.processedEventIds.add(event.id);this.processStreamLine(event.payload);this.activeEventSeq=Math.max(this.activeEventSeq,Number(event.seq)||0);}}else for (const line of data.lines || []) this.processStreamLine(line);
        if(Number(data.nextSeq)>this.activeEventSeq)this.activeEventSeq=Number(data.nextSeq);
        if(data.status?.state==='paused'){this.paused=true;this.status='任务已暂停';}else if(data.status?.state==='running'&&this.paused){this.paused=false;this.status='任务已继续';}else if (data.status?.state === 'completed') await this.finishChat(true); else if (data.status?.state === 'failed') {if(this.streamMessage&&data.status.fallbackDecision)this.streamMessage.fallbackDecision=data.status.fallbackDecision;await this.finishChat(false, data.error || data.status.message);}
      } catch (error) {
        this.pollFailures+=1;
        if(this.pollFailures>=5)await this.finishChat(false,`本地后端连续响应失败：${error.message}`);
        else this.status=`本地后端暂时中断，正在重试（${this.pollFailures}/4）`;
      }finally{this.polling=false;if(this.activeJob)this.schedulePoll(Math.min(1200,220*this.pollFailures+220));}
    },
    processStreamLine(line) {
      let obj; try { obj = JSON.parse(line); } catch (_) { return; }
      if (obj.type === 'system') {
        if (obj.subtype === 'init') { this.status = '环境已就绪';if(obj.session_id&&this.streamMessage){this.streamMessage.claudeSessionId=obj.session_id;const session=this.sessions.find(item=>item.id===this.activeSessionId);if(session)session.claudeSessionId=obj.session_id;} this.streamMessage.workflow.push({id:crypto.randomUUID(), title:`已加载 ${obj.tools?.length||0} 个工具 · ${obj.skills?.length||0} 个 Skill`, detail:`Agents: ${obj.agents?.length||0}`, state:'done', open:false}); }
        else if (obj.subtype === 'status') this.status = obj.status === 'requesting' ? '正在请求模型…' : obj.status;
        else if (obj.subtype === 'thinking_tokens') this.status = `正在思考 · ${this.number(obj.estimated_tokens)} Token`;
        return;
      }
      if (obj.type === 'stream_event') {
        const ev = obj.event || {};
        if (ev.type === 'content_block_start') {
          if (ev.content_block?.type === 'tool_use') { this.streamTool = {id:crypto.randomUUID(), toolUseId:ev.content_block.id, title:this.toolName(ev.content_block.name), detail:'', state:'running', open:false, rawName:ev.content_block.name}; this.streamMessage.workflow.push(this.streamTool); this.status = `正在${this.toolName(ev.content_block.name)}…`; }
          else if (ev.content_block?.type === 'thinking') this.status = '正在深度思考…';
        } else if (ev.type === 'content_block_delta') {
          if (ev.delta?.type === 'text_delta') this.appendStreamText(ev.delta.text||'');
          else if (ev.delta?.type === 'input_json_delta' && this.streamTool) {this.streamTool.detail += ev.delta.partial_json || '';if(this.streamTool.detail.length>12000)this.streamTool.detail='…已省略较早的工具参数…\n'+this.streamTool.detail.slice(-10000);}
        } else if (ev.type === 'content_block_stop' && this.streamTool) { if(this.streamTool.state==='running')this.streamTool.state='waiting'; this.streamTool = null; }
        this.scrollBottom(); return;
      }
      if(obj.type==='assistant'){
        for(const block of obj.message?.content||[]){if(block.type!=='tool_use'||!this.streamMessage)continue;let flow=(this.streamMessage.workflow||[]).find(item=>item.toolUseId===block.id);if(!flow){flow={id:crypto.randomUUID(),toolUseId:block.id,title:this.toolName(block.name),detail:this.limitToolOutput(JSON.stringify(block.input||{},null,2),12000),state:'waiting',open:false,rawName:block.name};this.streamMessage.workflow.push(flow);}else if(!flow.detail&&block.input)flow.detail=this.limitToolOutput(JSON.stringify(block.input,null,2),12000);}this.scrollBottom();return;
      }
      if(obj.type==='user'){
        for(const block of obj.message?.content||[]){if(block.type==='tool_result')this.applyToolResult(block);}this.scrollBottom();return;
      }
      if (obj.type === 'result') { this.finalResult = obj; if (!this.streamMessage.text.trim()) this.streamMessage.text = obj.result || ''; }
    },
    toolName(name) { return ({Read:'读取文件',Write:'写入文件',Edit:'修改文件',Bash:'执行命令',Glob:'查找文件',Grep:'搜索内容',WebSearch:'网络搜索',WebFetch:'读取网页',Task:'运行 Agent',Agent:'运行 Agent',Skill:'运行 Skill'})[name] || `调用 ${name}`; },
    toolResultText(content){if(typeof content==='string')return content;if(!Array.isArray(content))return content==null?'':JSON.stringify(content,null,2);return content.map(block=>{if(typeof block==='string')return block;if(block?.type==='text')return block.text||'';if(block?.type==='image')return '[工具返回了图片]';return JSON.stringify(block,null,2);}).filter(Boolean).join('\n');},
    appendStreamText(text){if(!text)return;this.streamTextBuffer+=text;if(this.streamFlushHandle)return;this.streamFlushHandle=setTimeout(()=>this.flushStreamText(),32);},
    flushStreamText(){clearTimeout(this.streamFlushHandle);this.streamFlushHandle=0;if(this.streamMessage&&this.streamTextBuffer)this.streamMessage.text+=this.streamTextBuffer;this.streamTextBuffer='';},
    limitToolOutput(text,max=40000){const value=text||'';return value.length<=max?value:`${value.slice(0,30000)}\n\n… 已省略 ${this.number(value.length-max)} 个字符 …\n\n${value.slice(-10000)}`;},
    applyToolResult(block){let flow=(this.streamMessage?.workflow||[]).find(item=>item.toolUseId===block.tool_use_id);if(!flow&&this.streamMessage){flow={id:crypto.randomUUID(),toolUseId:block.tool_use_id,title:'工具结果',detail:'',state:'waiting',open:false,rawName:'tool'};this.streamMessage.workflow.push(flow);}if(!flow)return;const output=this.limitToolOutput(this.toolResultText(block.content));const input=(flow.detail||'').trim();const fallback=block.is_error?'执行失败，但上游没有返回错误内容':'执行成功，但没有标准输出';flow.detail=`${input?`请求参数\n${input}\n\n`:''}${block.is_error?'错误输出':'执行输出'}\n${output||fallback}`;flow.state=block.is_error?'failed':'done';flow.open=!!block.is_error;},
    async finishChat(success, error='', options={}) {
      this.flushStreamText();
      const completedJob=this.activeJob;let taskReview=null;
      if(success&&completedJob){taskReview=await this.api(`/api/workbench/task/isolation?jobId=${encodeURIComponent(completedJob)}`).catch(()=>null);if(taskReview?.changes)taskReview.changes=taskReview.changes.map(item=>({...item,selected:true}));}
      clearTimeout(this.pollHandle); this.pollHandle = null; this.activeJob = ''; this.activeEventSeq=0;this.processedEventIds.clear();this.busy = false;this.paused=false; this.pollFailures = 0;
      if(!this.streamMessage){this.regenerationContext=null;this.recoveringJob=false;if(options.continueQueue&&this.currentSession?.queue?.length)setTimeout(()=>this.runNextQueued(),120);return;}
      const recovered=this.recoveringJob;this.recoveringJob=false;
      const regenerating=this.regenerationContext&&this.streamMessage?.id===this.regenerationContext.messageId;
      if (!success&&regenerating) {
        const context=this.regenerationContext,message=this.streamMessage,failedIndex=Number(message.activeVariant||0);message.variants.splice(failedIndex,1);this.applyVariant(message,Math.min(context.previousIndex,message.variants.length-1));const session=this.sessions.find(item=>item.id===this.activeSessionId);if(session){session.claudeSessionId=context.previousClaudeSessionId||session.id;session.started=true;}this.status='重新生成失败：'+(error||'请求失败');
      }
      else if (!success) { this.streamMessage.role='system'; this.streamMessage.text = error || '请求失败'; this.status=options.continueQueue?'正在切换方向':'请求失败'; }
      else {
        const usage = this.finalResult?.usage || {}; const input = Number(usage.input_tokens||0) + Number(usage.cache_read_input_tokens||0) + Number(usage.cache_creation_input_tokens||0), output = Number(usage.output_tokens||0);
        this.streamMessage.usage = {input, output, total:input+output, cost:Number(this.finalResult?.total_cost_usd||0)}; this.streamMessage.streaming=false; this.status='已完成';
        const session=this.sessions.find(s=>s.id===this.activeSessionId); if(session){session.started=true;session.updatedAt=new Date().toISOString();}
        if(this.streamMessage.variants?.length)this.syncActiveVariant(this.streamMessage);
      }
      this.streamMessage.streaming=false; this.streamMessage=null; this.streamTool=null;this.regenerationContext=null; await this.saveMessages(); await this.saveSessions();
      if(success&&!regenerating)await this.reloadActiveTranscript();if(taskReview&&taskReview.kind!=='direct-readonly'){const target=[...this.messages].reverse().find(item=>item.role==='assistant');if(target){target.taskReview=taskReview;await this.saveMessages();}}this.scrollBottom();setTimeout(()=>this.syncQueue(),180);
    },
    async retryWithProvider(candidate,message){if(this.busy||!candidate?.providerId)return;const index=this.messages.findIndex(item=>item.id===message.id),user=[...this.messages.slice(0,index)].reverse().find(item=>item.role==='user');if(!user)return;if(!confirm(`切换到“${candidate.name} / ${candidate.models?.[0]||'默认模型'}”并重新发送上一条请求？\n\n只有确认后才会调用该 API。`))return;this.settings.providerId=candidate.providerId;this.settings.model=candidate.models?.[0]||'';await this.saveSettings();this.attachments=[...(user.attachments||[])];this.prompt=user.text||'';this.status='已切换 Provider，正在按原请求重试…';await this.sendPrompt();},
    async stopChat(options={}) { if (!this.activeJob) return;const jobId=this.activeJob;clearTimeout(this.pollHandle);this.pollHandle=null;await this.api(`/api/chat/stop/${jobId}`, {method:'POST'}).catch(()=>{});await this.finishChat(false, options.reason||'已停止本次生成。',options); },
    async pauseChat(){if(!this.activeJob||this.paused)return;try{await this.api(`/api/chat/pause/${this.activeJob}`,{method:'POST'});this.paused=true;this.status='任务已暂停；可关闭窗口，Host 会保留状态';}catch(error){this.status='暂停失败：'+error.message;}},
    async resumeChat(){if(!this.activeJob||!this.paused)return;try{await this.api(`/api/chat/resume/${this.activeJob}`,{method:'POST'});this.paused=false;this.status='任务已继续';this.schedulePoll(80);}catch(error){this.status='继续任务失败：'+error.message;}},
    async loadApprovals(){this.approvals=await this.api('/api/permissions/pending').catch(()=>this.approvals);},
    async respondApproval(item,behavior){if(this.approvalBusy)return;this.approvalBusy=item.id;try{await this.api('/api/permissions/respond',{method:'POST',body:{id:item.id,behavior,message:behavior==='deny'?'用户在图形界面中拒绝了此操作':''}});this.approvals=this.approvals.filter(value=>value.id!==item.id);this.status=behavior==='allow'?`已允许 ${item.toolName}`:`已拒绝 ${item.toolName}`;}catch(error){this.status='权限响应失败：'+error.message;}finally{this.approvalBusy='';}},
    approvalInput(item){try{return JSON.stringify(item.input||{},null,2);}catch(_){return String(item.input||'');}},
    async applyTaskChanges(message){const review=message?.taskReview;if(!review||review.applying)return;review.applying=true;try{const paths=(review.changes||[]).filter(item=>item.selected).map(item=>item.path);if(!paths.length){this.status='请至少选择一个要应用的文件';return;}await this.api('/api/workbench/task/apply',{method:'POST',body:{jobId:review.jobId,paths}});review.state='applied';this.status=`已将 ${paths.length} 个文件的变更应用到原工作区`;await this.saveMessages();}catch(error){this.status='应用变更失败：'+error.message;}finally{review.applying=false;}},
    async revertTaskChanges(message){const review=message?.taskReview;if(!review||review.applying||!confirm('撤销此任务隔离区中的全部变更？原工作区不会被修改。'))return;review.applying=true;try{await this.api('/api/workbench/task/revert',{method:'POST',body:{jobId:review.jobId}});review.state='reverted';for(const item of review.changes||[])item.selected=false;this.status='任务变更已撤销';await this.saveMessages();}catch(error){this.status='撤销失败：'+error.message;}finally{review.applying=false;}},
    async reloadActiveTranscript(){const session=this.currentSession;if(!session?.started)return;const loaded=(await this.api(`/api/workbench/transcripts/${session.claudeSessionId||session.id}?workspace=${encodeURIComponent(session.workspace||this.workspace)}`).catch(()=>null))?.messages;if(loaded?.length){this.messages=loaded;await this.saveMessages();}},
    showOlderMessages(){const end=this.messageWindowEnd||this.messages.length;this.messageWindowEnd=Math.max(this.messageWindowSize,end-this.messageWindowSize);nextTick(()=>{const el=this.$refs.conversation;if(el)el.scrollTop=0;});},
    showNewerMessages(){this.messageWindowEnd=Math.min(this.messages.length,(this.messageWindowEnd||0)+this.messageWindowSize);if(!this.hasNewerMessages)this.scrollBottom();},
    scrollBottom() {this.messageWindowEnd=this.messages.length;if(!this.messages.length)return;nextTick(()=>{const el=this.$refs.conversation;if(el)el.scrollTop=el.scrollHeight;}); },
    openSettings(tab='api'){this.settingsTab=tab;this.archivePreview=null;this.openProviderModal(this.selectedProvider);},
    openProviderModal(provider=null) {
      this.providerError='';this.discoverMessage='';
      if (provider) this.providerForm={id:provider.id,preset:presetForBaseUrl(provider.text?.baseUrl),name:provider.name,token:'',authStyle:provider.authStyle||'auto',capabilities:provider.capabilities||{schemaVersion:1,models:{},evidencePolicy:'unknown-until-probed'},text:{...provider.text,modelsText:(provider.text.models||[]).join(', ')},image:{...provider.image,modelsText:(provider.image.models||[]).join(', ')}};
      else this.providerForm=blankProvider();
      this.providerModal=true;
    },
    resetProviderForm(){this.providerError='';this.discoverMessage='';this.providerForm=blankProvider();},
    closeProviderModal() { this.providerModal=false;this.archivePreview=null; },
    modelsFromText(value) { return value.split(/[,，;；\n]+/).map(x=>x.trim()).filter(Boolean); },
    providerPresetChanged() {
      const preset=providerPresets[this.providerForm.preset];if(!preset)return;
      this.providerError='';this.discoverMessage='';this.providerForm.name=preset.name;this.providerForm.authStyle=preset.authStyle;
      Object.assign(this.providerForm.text,{enabled:true,protocol:preset.textProtocol,baseUrl:preset.textBaseUrl,modelsText:''});
      Object.assign(this.providerForm.image,{enabled:!!preset.imageBaseUrl,protocol:'openai-images',baseUrl:preset.imageBaseUrl,modelsText:''});
    },
    async autoConfigureProvider() {
      this.providerError='';this.discoverMessage='';
      if(!this.providerForm.token&&!this.providerForm.id){this.providerError='请先粘贴 API 令牌';return;}
      if(!providerPresets[this.providerForm.preset]){this.providerError='未知 Key 无法安全地自动匹配服务商；请选择服务商预设，或使用“自定义 / 其他”填写接口地址';return;}
      this.discovering='all';
      try {
        const data=await this.api('/api/providers/probe',{method:'POST',body:{preset:this.providerForm.preset,providerId:this.providerForm.id,token:this.providerForm.token}});
        this.providerForm.name=data.name;this.providerForm.authStyle=data.authStyle;
        Object.assign(this.providerForm.text,{enabled:data.text.enabled,protocol:data.text.protocol,baseUrl:data.text.baseUrl,modelsText:(data.text.models||[]).join(', ')});
        Object.assign(this.providerForm.image,{enabled:data.image.enabled,protocol:data.image.protocol,baseUrl:data.image.baseUrl,modelsText:(data.image.models||[]).join(', ')});
        this.providerForm.capabilities=data.capabilities||{schemaVersion:1,models:{},evidencePolicy:'unknown-until-probed'};
        this.discoverMessage=`识别成功：Text ${data.text.models.length} 个 · Image ${data.image.models.length} 个模型。${data.warning?data.warning+' ':''}确认后点击“加密保存”。`;
      }catch(error){this.providerError=`${providerPresets[this.providerForm.preset]?.name||'API'} 自动识别失败：${error.message}`;}finally{this.discovering='';}
    },
    async discover(kind) {
      this.providerError='';this.discoverMessage='';const cap=this.providerForm[kind];if(!cap.baseUrl){this.providerError=`请先填写${kind==='text'?'文字':'生图'}接口地址`;return;}if(!this.providerForm.token&&!this.providerForm.id){this.providerError='首次导入时请先填写 API 令牌';return;}
      this.discovering=kind;
      try { const data=await this.api('/api/providers/discover',{method:'POST',body:{providerId:this.providerForm.id,token:this.providerForm.token,baseUrl:cap.baseUrl,authStyle:this.providerForm.authStyle}}); const models=kind==='image'?(data.imageModels.length?data.imageModels:data.models):(data.textModels.length?data.textModels:data.models);cap.modelsText=models.join(', ');this.discoverMessage=`已从 ${data.url} 读取 ${data.models.length} 个模型，已填入 ${kind==='text'?'Text':'Image'} capability。`; }
      catch(error){this.providerError='读取模型失败：'+error.message;}finally{this.discovering='';}
    },
    async saveProvider() {
      this.providerError='';this.savingProvider=true;
      try { const payload={id:this.providerForm.id||undefined,name:this.providerForm.name,token:this.providerForm.token,authStyle:this.providerForm.authStyle,capabilities:this.providerForm.capabilities,text:{enabled:this.providerForm.text.enabled,protocol:this.providerForm.text.protocol,baseUrl:this.providerForm.text.baseUrl,models:this.modelsFromText(this.providerForm.text.modelsText)},image:{enabled:this.providerForm.image.enabled,protocol:this.providerForm.image.protocol,baseUrl:this.providerForm.image.baseUrl,models:this.modelsFromText(this.providerForm.image.modelsText)}}; const saved=await this.api('/api/providers',{method:'POST',body:payload});const index=this.providers.findIndex(p=>p.id===saved.id);if(index<0)this.providers.push(saved);else this.providers.splice(index,1,saved);this.settings.providerId=saved.id;this.ensureModels();await this.saveSettings();this.providerModal=false; }
      catch(error){this.providerError=error.message;}finally{this.savingProvider=false;}
    },
    async deleteProvider(){if(!confirm('删除这个 API 配置？令牌和模型配置都会从本机令牌库移除。'))return;await this.api(`/api/providers/${this.providerForm.id}`,{method:'DELETE'});this.providers=this.providers.filter(p=>p.id!==this.providerForm.id);this.settings.providerId=this.providers[0]?.id||'';this.ensureModels();this.providerModal=false;await this.saveSettings();},
    toolList(value){return String(value||'').split(/[,，;；\n]+/).map(item=>item.trim()).filter(Boolean);},
    saveDraft(){if(this.activeSessionId)localStorage.setItem(`claude-draft:${this.activeSessionId}`,this.prompt||'');},
    useSlashCommand(name){this.prompt=`/${name} `;nextTick(()=>document.querySelector('.composer textarea')?.focus());},
    escapeHtml(value){const node=document.createElement('div');node.textContent=value??'';return node.innerHTML;},
    localFileMarkup(path,label='',compact=false){const normalized=String(path||'').replace(/^file:\/\/\//i,'').replace(/\//g,'\\'),safePath=this.escapeHtml(normalized),safeLabel=this.escapeHtml(label||this.attachmentName(normalized)),kind=this.fileKind(normalized),type=this.fileTypeClass(normalized),encoded=encodeURIComponent(normalized);return `<button type="button" class="local-document-link${compact?' compact':''}" data-local-path="${encoded}" title="打开本地文件：${safePath}"><span class="file-visual ${type}"><i>${this.fileIcon(normalized)}</i></span><span class="file-copy"><b>${safeLabel}</b><small>${this.escapeHtml(kind)} · ${safePath}</small></span><em>打开</em></button>`;},
    handleMessageBodyClick(event){const target=event.target?.closest?.('[data-local-path]');if(!target)return;event.preventDefault();event.stopPropagation();try{this.openLocalFile(decodeURIComponent(target.dataset.localPath||''));}catch(_){this.status='本地文件地址无效';}},
    renderMarkdown(value){
      let source=value||'',blocks=[],locals=[];
      source=source.replace(/```([^\n]*)\n([\s\S]*?)```/g,(_,lang,code)=>{const id=blocks.length;blocks.push(`<pre class="code-block"><span>${this.escapeHtml(lang||'code')}</span><code>${this.escapeHtml(code)}</code></pre>`);return `\n@@BLOCK${id}@@\n`;});
      source=source.replace(/\[([^\]]+)\]\(((?:file:\/\/\/)?[A-Za-z]:[\\/][^)]+)\)/gi,(_,label,path)=>{const id=locals.length;locals.push(this.localFileMarkup(path,label));return `@@LOCAL${id}@@`;});
      source=source.replace(/`((?:file:\/\/\/)?[A-Za-z]:[\\/][^`\n]+)`/gi,(_,path)=>{const id=locals.length;locals.push(this.localFileMarkup(path,'',true));return `@@LOCAL${id}@@`;});
      let text=this.escapeHtml(source);
      text=text.replace(/^######\s+(.+)$/gm,'<h6>$1</h6>').replace(/^#####\s+(.+)$/gm,'<h5>$1</h5>').replace(/^####\s+(.+)$/gm,'<h4>$1</h4>').replace(/^###\s+(.+)$/gm,'<h3>$1</h3>').replace(/^##\s+(.+)$/gm,'<h2>$1</h2>').replace(/^#\s+(.+)$/gm,'<h1>$1</h1>');
      text=text.replace(/`([^`\n]+)`/g,'<code class="inline-code">$1</code>').replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>').replace(/(?<!\*)\*([^*\n]+)\*/g,'<em>$1</em>').replace(/~~([^~]+)~~/g,'<del>$1</del>');
      text=text.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g,'<a href="$2" target="_blank" rel="noreferrer">$1</a>');
      text=text.replace(/(^|[\s>])([A-Za-z]:[\\/][^<>\r\n]*?\.(?:docx?|xlsx?|pptx?|pdf|txt|md|csv|json|html?|css|js|ts|tsx|jsx|vue|cs|py|ps1|java|go|rs|cpp|c|h|xml|ya?ml|sql|png|jpe?g|webp|gif|bmp))(?=$|[\s<，。；、])/g,(match,prefix,path)=>{const id=locals.length;locals.push(this.localFileMarkup(path));return `${prefix}@@LOCAL${id}@@`;});
      text=text.replace(/^&gt;\s?(.*)$/gm,'<blockquote>$1</blockquote>').replace(/^[-*]\s+(.+)$/gm,'<div class="md-list">• <span>$1</span></div>').replace(/^\d+\.\s+(.+)$/gm,'<div class="md-list numbered"><span>$1</span></div>');
      text=text.replace(/\n/g,'<br>');blocks.forEach((block,index)=>{text=text.replace(`@@BLOCK${index}@@`,block);});locals.forEach((block,index)=>{text=text.replace(`@@LOCAL${index}@@`,block);});return text;
    },
    async openInspector(tab){this.inspector.open=true;this.inspector.tab=tab;if(tab==='terminal'){await nextTick();await this.ensureTerminal();await this.resizeTerminal();}await this.refreshInspector();},
    async refreshInspector(){const tab=this.inspector.tab;try{if(tab==='files')await this.loadTree(this.tree.path||'');else if(tab==='git')await this.refreshGit();else if(tab==='search')await this.searchTranscripts();else if(tab==='extensions')await this.loadExtensions();else if(tab==='usage')await this.loadUsage();else if(tab==='health')await this.loadHealth();else if(tab==='checkpoints')await this.loadCheckpoints();else if(tab==='terminal')await this.ensureTerminal();else if(tab==='schedule')await this.loadSchedules();}catch(error){this.status=`加载${tab}失败：${error.message}`;}},
    async loadTree(path=''){this.tree.loading=true;try{const data=await this.api(`/api/workbench/tree?workspace=${encodeURIComponent(this.workspace)}&path=${encodeURIComponent(path)}`);this.tree={path:data.path||'',entries:data.entries||[],loading:false};}finally{this.tree.loading=false;}},
    treeParent(){const parts=(this.tree.path||'').split(/[\\/]/).filter(Boolean);parts.pop();this.loadTree(parts.join('\\'));},
    async openTreeEntry(entry){if(entry.directory){await this.loadTree(entry.path);return;}try{const data=await this.api(`/api/workbench/file?workspace=${encodeURIComponent(this.workspace)}&path=${encodeURIComponent(entry.path)}`);this.selectedFile=data;this.fileContent=data.content||'';this.fileDirty=false;}catch(error){this.status='文件预览失败：'+error.message;}},
    async saveSelectedFile(){if(!this.selectedFile||!this.fileDirty)return;try{await this.api('/api/workbench/file',{method:'POST',body:{workspace:this.workspace,path:this.selectedFile.path,content:this.fileContent}});this.fileDirty=false;this.status=`已保存 ${this.selectedFile.path}（同时创建 .bak）`;await this.refreshGit();}catch(error){this.status='保存失败：'+error.message;}},
    async refreshGit(){this.git.loading=true;try{const data=await this.api(`/api/workbench/git/status?workspace=${encodeURIComponent(this.workspace)}`);Object.assign(this.git,data);await this.loadGitDiff(this.git.staged);}finally{this.git.loading=false;}},
    async loadGitDiff(staged=false){this.git.staged=!!staged;const data=await this.api(`/api/workbench/git/diff?workspace=${encodeURIComponent(this.workspace)}&staged=${staged?1:0}`);this.git.diff=data.diff||data.error||'没有差异';},
    async gitAction(action,paths=[]){if(action==='commit'&&!this.git.commitMessage.trim())return;try{const body={workspace:this.workspace,action,paths,message:this.git.commitMessage};await this.api('/api/workbench/git/action',{method:'POST',body});if(action==='commit')this.git.commitMessage='';this.status=`Git ${action} 已完成`;await this.refreshGit();}catch(error){this.status=`Git ${action} 失败：${error.message}`;}},
    async searchTranscripts(){this.transcriptSearching=true;try{this.transcriptResults=await this.api(`/api/workbench/transcripts?query=${encodeURIComponent(this.transcriptQuery)}`);}finally{this.transcriptSearching=false;}},
    async openTranscriptResult(item){let session=this.sessions.find(value=>value.id===item.id);if(!session){session={id:item.id,claudeSessionId:item.id,title:item.title,workspace:item.workspace,createdAt:item.createdAt,updatedAt:item.updatedAt,started:true,transcript:true,allowedDirs:[],queue:[]};this.sessions.push(session);}this.workspace=item.workspace;this.inspector.open=false;await this.activateSession(item.id,true);},
    async loadExtensions(){this.extensions=await this.api(`/api/workbench/extensions?workspace=${encodeURIComponent(this.workspace)}`);},
    async scaffoldExtension(type){const label=type==='skill'?'Skill':'Subagent',name=window.prompt(`新建项目级 ${label}\n名称只能使用字母、数字、- 和 _`,'');if(name===null||!name.trim())return;try{const data=await this.api('/api/workbench/extensions/scaffold',{method:'POST',body:{workspace:this.workspace,type,name:name.trim()}});await this.loadExtensions();this.status=`已创建 ${label}：${data.name}`;await this.openLocalFile(data.path);}catch(error){this.status=`创建 ${label} 失败：${error.message}`;}},
    async importExtension(){this.extensionStatus='正在校验扩展包签名与兼容版本…';try{const data=await this.api('/api/workbench/extensions/import',{method:'POST',body:{workspace:this.workspace}});if(data.cancelled){this.extensionStatus='已取消导入';return;}await this.loadExtensions();this.extensionStatus=`已安装 ${data.type==='skill'?'Skill':'Subagent'}：${data.name} · ${data.signatureStatus}`;}catch(error){this.extensionStatus='扩展包被拒绝：'+error.message;}},
    async deleteExtension(type,item){if(!item||item.scope!=='project'||!confirm(`删除项目级 ${type==='skill'?'Skill':'Agent'}“${item.name}”？\n\n这会删除工作区 .claude 下对应文件。`))return;try{await this.api('/api/workbench/extensions/delete',{method:'POST',body:{workspace:this.workspace,type,name:item.name}});await this.loadExtensions();this.status=`已删除 ${item.name}`;}catch(error){this.status='删除扩展失败：'+error.message;}},
    async pluginAction(action){let name='';if(action!=='list'){name=(window.prompt(`${action==='install'?'安装':action==='update'?'更新':'卸载'} Plugin\n请输入 plugin@marketplace 标识`,'')||'').trim();if(!name)return;if(!/^[\w@./-]+$/.test(name)){this.extensionStatus='Plugin 标识只能包含字母、数字、@、点、斜杠、下划线和连字符';return;}}this.extensionStatus=`正在执行 claude plugin ${action}…`;try{const executable='"D:\\softwares\\ClaudeCode\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe"',command=`${executable} plugin ${action}${name?' '+name:''}`;const data=await this.api('/api/workbench/terminal',{method:'POST',body:{workspace:this.workspace,command}});this.extensionStatus=(data.output||data.error||`exit ${data.exitCode}`).trim();await this.loadExtensions();}catch(error){this.extensionStatus=error.message;}},
    async testMcp(){this.extensionStatus='正在调用 claude mcp list…';try{const data=await this.api('/api/workbench/terminal',{method:'POST',body:{workspace:this.workspace,command:'"D:\\softwares\\ClaudeCode\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe" mcp list'}});this.extensionStatus=(data.output||data.error||`exit ${data.exitCode}`).trim();}catch(error){this.extensionStatus=error.message;}},
    async loadUsage(){this.usage=await this.api(`/api/workbench/usage?workspace=${encodeURIComponent(this.workspace)}`);},
    async loadHealth(){this.health=await this.api(`/api/workbench/health?workspace=${encodeURIComponent(this.workspace)}`);},
    async exportDiagnostics(){try{this.status='正在生成已脱敏的本地诊断包…';const data=await this.api('/api/workbench/diagnostics/export',{method:'POST',body:{workspace:this.workspace}});this.health={...this.health,diagnosticBundle:data.path,diagnosticRedacted:data.redacted};this.status='诊断包已生成（仅保存在本机）';}catch(error){this.status='生成诊断包失败：'+error.message;}},
    async loadCheckpoints(){if(!this.currentSession?.started){this.checkpoints=[];return;}this.checkpoints=await this.api(`/api/workbench/checkpoints?sessionId=${encodeURIComponent(this.currentSession.claudeSessionId||this.currentSession.id)}`).catch(()=>[]);},
    async createCheckpoint(){if(!this.currentSession?.started)return;const label=window.prompt('检查点名称','手动检查点');if(label===null)return;try{await this.api('/api/workbench/checkpoints',{method:'POST',body:{sessionId:this.currentSession.claudeSessionId||this.currentSession.id,workspace:this.currentSession.workspace||this.workspace,label}});await this.loadCheckpoints();this.status='检查点已保存';}catch(error){this.status='创建检查点失败：'+error.message;}},
    async restoreCheckpoint(item,fork=false){if(!confirm(fork?'从此检查点创建一个新的会话分支？':'恢复后会覆盖当前 transcript；系统会自动保留恢复前备份。继续吗？'))return;try{const data=await this.api('/api/workbench/checkpoints/restore',{method:'POST',body:{sessionId:this.currentSession.claudeSessionId||this.currentSession.id,workspace:this.currentSession.workspace||this.workspace,checkpointId:item.id,fork}});if(fork){await this.syncTranscripts();await this.activateSession(data.sessionId,true);}else await this.activateSession(this.activeSessionId,true);this.status=fork?'已创建会话分支':'已恢复检查点';}catch(error){this.status='恢复检查点失败：'+error.message;}},
    async handleClipboardPaste(event){const items=[...(event.clipboardData?.items||[])],image=items.find(item=>item.type?.startsWith('image/'));if(!image||!this.canAttachFiles)return;event.preventDefault();const file=image.getAsFile();if(!file)return;const reader=new FileReader();reader.onload=async()=>{try{const data=await this.api('/api/workbench/clipboard',{method:'POST',body:{mime:file.type,data:reader.result}});const reason=this.attachmentUnsupportedReason(data.path);if(reason){this.attachmentNotice=reason;return;}this.attachments.push(data.path);this.loadAttachmentPreviews([data.path]);this.status='已从剪贴板添加图片';}catch(error){this.status='粘贴图片失败：'+error.message;}};reader.readAsDataURL(file);},
    async ensureXtermView(){
      await nextTick();const host=this.$refs.xtermHost;if(!host||!globalThis.Terminal)return;
      if(terminalView&&terminalView.element?.parentElement!==host){terminalView.dispose();terminalView=null;terminalFitAddon=null;terminalSearchAddon=null;}
      if(!terminalView){
        try{
        terminalView=new globalThis.Terminal({cursorBlink:true,cursorStyle:'bar',fontFamily:'Cascadia Mono, Consolas, monospace',fontSize:11,lineHeight:1.18,scrollback:10000,allowProposedApi:false,convertEol:false,theme:{background:'#151716',foreground:'#c4cfcb',cursor:'#df8b69',selectionBackground:'#586a6455',black:'#1b1d1c',red:'#d06c68',green:'#7ca982',yellow:'#c7a56a',blue:'#7093b8',magenta:'#aa84b8',cyan:'#70aaa3',white:'#d7ddd9'}});
        const FitCtor=globalThis.FitAddon?.FitAddon||globalThis.FitAddon,SearchCtor=globalThis.SearchAddon?.SearchAddon||globalThis.SearchAddon;
        if(typeof FitCtor==='function'){terminalFitAddon=new FitCtor();terminalView.loadAddon(terminalFitAddon);}
        if(typeof SearchCtor==='function'){terminalSearchAddon=new SearchCtor();terminalView.loadAddon(terminalSearchAddon);terminalSearchAddon.onDidChangeResults(value=>{this.terminalSearchResult={resultIndex:value.resultIndex,total:value.resultCount};});}
        terminalView.open(host);
        if(this.terminalOutput)terminalView.write(this.terminalOutput);
        terminalView.onData(data=>{if(!this.terminalId)return;terminalInputQueue=terminalInputQueue.then(()=>this.api('/api/workbench/terminal/write',{method:'POST',body:{id:this.terminalId,command:data,submit:false}})).catch(error=>{this.status='终端输入失败：'+error.message;});});
        terminalView.onResize(size=>{this.terminalColumns=size.cols;this.terminalRows=size.rows;if(this.terminalId)this.api('/api/workbench/terminal/resize',{method:'POST',body:{id:this.terminalId,columns:size.cols,rows:size.rows}}).catch(()=>{});});
        }catch(error){terminalView=null;terminalFitAddon=null;terminalSearchAddon=null;this.terminalOutput='xterm.js 初始化失败：'+(error?.message||error);host.textContent=this.terminalOutput;host.classList.add('terminal-init-error');this.status=this.terminalOutput;return;}
      }
      terminalFitAddon?.fit();terminalView.focus();
    },
    searchTerminal(previous=false){if(!terminalSearchAddon)return;const value=this.terminalSearch||'';if(!value){this.clearTerminalSearch();return;}const options={caseSensitive:false,incremental:true,decorations:{matchBackground:'#67584b',activeMatchBackground:'#c87855',matchBorder:'#8e7561',activeMatchBorder:'#f1ae8e'}};if(previous)terminalSearchAddon.findPrevious(value,options);else terminalSearchAddon.findNext(value,options);},
    clearTerminalSearch(){this.terminalSearch='';this.terminalSearchResult={resultIndex:-1,total:0};terminalSearchAddon?.clearDecorations();terminalView?.clearSelection();},
    async ensureTerminal(){await this.ensureXtermView();if(this.terminalId&&this.terminalWorkspace.toLowerCase()===this.workspace.toLowerCase()){await this.resizeTerminal();return;}try{const data=await this.api('/api/workbench/terminal/start',{method:'POST',body:{workspace:this.workspace}});this.terminalId=data.id;this.terminalWorkspace=data.workspace;this.terminalGrid=[[]];this.terminalRow=0;this.terminalColumn=0;this.terminalOutput='';terminalView?.reset();await this.resizeTerminal();await this.pollTerminal(true);}catch(error){this.terminalOutput=`终端启动失败：${error.message}`;terminalView?.writeln('\r\n'+this.terminalOutput);}},
    async resizeTerminal(){if(!this.terminalId)return;await nextTick();if(terminalFitAddon&&terminalView){terminalFitAddon.fit();const columns=terminalView.cols,rows=terminalView.rows;if(columns===this.terminalColumns&&rows===this.terminalRows)return;this.terminalColumns=columns;this.terminalRows=rows;try{await this.api('/api/workbench/terminal/resize',{method:'POST',body:{id:this.terminalId,columns,rows}});}catch(_){}return;}const node=document.querySelector('.terminal-output');if(!node)return;const style=getComputedStyle(node),font=parseFloat(style.fontSize)||10,line=parseFloat(style.lineHeight)||16,columns=Math.max(40,Math.min(300,Math.floor((node.clientWidth-24)/(font*.61)))),rows=Math.max(12,Math.min(120,Math.floor((node.clientHeight-20)/line)));if(columns===this.terminalColumns&&rows===this.terminalRows)return;this.terminalColumns=columns;this.terminalRows=rows;try{await this.api('/api/workbench/terminal/resize',{method:'POST',body:{id:this.terminalId,columns,rows}});}catch(_){ }},
    terminalCharWidth(ch){const cp=ch.codePointAt(0);if(cp===0)return 0;if(cp>=0x1100&&(cp<=0x115f||cp===0x2329||cp===0x232a||(cp>=0x2e80&&cp<=0xa4cf&&cp!==0x303f)||(cp>=0xac00&&cp<=0xd7a3)||(cp>=0xf900&&cp<=0xfaff)||(cp>=0xfe10&&cp<=0xfe19)||(cp>=0xfe30&&cp<=0xfe6f)||(cp>=0xff00&&cp<=0xff60)||(cp>=0xffe0&&cp<=0xffe6)||(cp>=0x1f300&&cp<=0x1faff)||(cp>=0x20000&&cp<=0x3fffd)))return 2;return 1;},
    feedTerminal(value){
      let i=0;const ensure=row=>{while(this.terminalGrid.length<=row)this.terminalGrid.push([]);};
      while(i<value.length){
        if(value[i]==='\x1b'&&value[i+1]===']'){const bell=value.indexOf('\x07',i+2),st=value.indexOf('\x1b\\',i+2);i=bell>=0&&(st<0||bell<st)?bell+1:st>=0?st+2:value.length;continue;}
        if(value[i]==='\x1b'&&value[i+1]==='['){
          const match=value.slice(i).match(/^\x1b\[([0-9;?]*)([ -\/]*)?([@-~])/);
          if(match){const parts=match[1].replace(/^\?/,'').split(';').map(x=>parseInt(x||'0',10)),n=parts[0]||1,cmd=match[3];if(cmd==='H'||cmd==='f'){this.terminalRow=Math.max(0,(parts[0]||1)-1);this.terminalColumn=Math.max(0,(parts[1]||1)-1);ensure(this.terminalRow);}else if(cmd==='A')this.terminalRow=Math.max(0,this.terminalRow-n);else if(cmd==='B'){this.terminalRow+=n;ensure(this.terminalRow);}else if(cmd==='C')this.terminalColumn+=n;else if(cmd==='D')this.terminalColumn=Math.max(0,this.terminalColumn-n);else if(cmd==='G')this.terminalColumn=Math.max(0,n-1);else if(cmd==='J'&&(parts[0]===2||parts[0]===3)){this.terminalGrid=[[]];this.terminalRow=0;this.terminalColumn=0;}else if(cmd==='K'){ensure(this.terminalRow);if(parts[0]===2)this.terminalGrid[this.terminalRow]=[];else this.terminalGrid[this.terminalRow].splice(this.terminalColumn);}i+=match[0].length;continue;}
        }
        const cp=value.codePointAt(i),ch=String.fromCodePoint(cp);i+=ch.length;
        if(ch==='\r'){this.terminalColumn=0;continue;}if(ch==='\n'){this.terminalRow++;this.terminalColumn=0;ensure(this.terminalRow);continue;}
        if(ch==='\b'){this.terminalColumn=Math.max(0,this.terminalColumn-1);ensure(this.terminalRow);if(this.terminalGrid[this.terminalRow][this.terminalColumn]==='\u0000')this.terminalColumn=Math.max(0,this.terminalColumn-1);continue;}
        if(ch==='\t'){this.terminalColumn+=8-(this.terminalColumn%8);continue;}if(cp<32)continue;
        ensure(this.terminalRow);const line=this.terminalGrid[this.terminalRow];while(line.length<this.terminalColumn)line.push(' ');
        const width=this.terminalCharWidth(ch);line[this.terminalColumn]=ch;if(width===2)line[this.terminalColumn+1]='\u0000';this.terminalColumn+=width;
        if(this.terminalColumn>=this.terminalColumns){this.terminalRow++;this.terminalColumn=0;ensure(this.terminalRow);}
        if(this.terminalGrid.length>2000){const remove=this.terminalGrid.length-1600;this.terminalGrid.splice(0,remove);this.terminalRow=Math.max(0,this.terminalRow-remove);}
      }
      this.terminalOutput=this.terminalGrid.map(line=>line.filter(ch=>ch!=='\u0000').join('').replace(/\s+$/,'')).join('\n');
    },
    async pollTerminal(force=false){if(!this.terminalId||(!force&&(!this.inspector.open||this.inspector.tab!=='terminal')))return;try{const data=await this.api(`/api/workbench/terminal/poll?id=${encodeURIComponent(this.terminalId)}`);if(data.output){this.terminalOutput=(this.terminalOutput+data.output).slice(-1000000);if(terminalView)terminalView.write(data.output);else this.feedTerminal(data.output);}if(!data.alive)this.terminalId='';}catch(_){ }},
    async runTerminal(){const command=this.terminalCommand.trim();if(!command||this.terminalRunning)return;this.terminalRunning=true;this.terminalCommand='';try{await this.ensureTerminal();await this.api('/api/workbench/terminal/write',{method:'POST',body:{id:this.terminalId,command}});await this.pollTerminal(true);}catch(error){this.terminalOutput+=`错误：${error.message}\n`;}finally{this.terminalRunning=false;}},
    async sendTerminalKey(command){try{await this.ensureTerminal();await this.api('/api/workbench/terminal/write',{method:'POST',body:{id:this.terminalId,command,submit:false}});await this.pollTerminal(true);}catch(error){this.status=`终端交互失败：${error.message}`;}},
    saveSchedules(){const snapshot=JSON.parse(JSON.stringify(this.scheduler));localStorage.setItem('claude-workbench-scheduler',JSON.stringify(snapshot));return this.api('/api/workbench/schedules',{method:'POST',body:snapshot});},
    async loadSchedules(){const data=await this.api('/api/workbench/schedules').catch(()=>null);if(Array.isArray(data))this.scheduler=data;},
    async addSchedule(){const text=this.scheduleForm.text.trim(),at=this.scheduleForm.at;if(!text||!at)return;this.scheduler.push({id:crypto.randomUUID(),text,at:new Date(at).toISOString(),repeatMinutes:Number(this.scheduleForm.repeatMinutes||0),maxRetries:Number(this.scheduleForm.maxRetries??3),conflictPolicy:this.scheduleForm.conflictPolicy||'queue',workspace:this.workspace,sessionId:this.activeSessionId,enabled:true});await this.saveSchedules();this.scheduleForm={text:'',at:'',repeatMinutes:0,maxRetries:3,conflictPolicy:'queue'};await this.loadSchedules();this.status='后台调度已保存；窗口关闭到托盘后仍会执行';},
    async removeSchedule(id){this.scheduler=this.scheduler.filter(item=>item.id!==id);await this.saveSchedules();},
    async replaySchedule(item){try{await this.api('/api/workbench/schedules/replay',{method:'POST',body:{id:item.id}});await this.loadSchedules();this.status='失败任务已重新放入调度队列';}catch(error){this.status='重放失败：'+error.message;}},
    scheduleState(item){return ({scheduled:'等待执行',running:'正在领取',retry:'等待重试',completed:'已完成',dead_letter:'失败箱',disabled:'已停用'})[item?.state]||item?.state||'等待执行';},
    async openTarget(target) { await this.api(`/api/open/${target}`, {method:'POST'}).catch(error=>alert(error.message)); },
    async generateImage() {
      if (!this.imageForm.prompt.trim() || this.imageBusy) return;if(!confirm('这会调用第三方图像接口，可能产生 API 费用。确定开始生成吗？'))return;
      this.imageBusy=true;this.imageStatus='正在生成图片…';this.imageResult='';
      try { const data=await this.api('/api/image/start',{method:'POST',body:{providerId:this.settings.providerId,model:this.imageForm.model,prompt:this.imageForm.prompt,size:this.imageForm.size}});const poll=async()=>{try{const state=await this.api(`/api/image/poll/${data.jobId}`);if(state.state==='completed'){this.imageBusy=false;this.imageResult=`/api/image/file/${data.jobId}`;const usage=state.usage?.total_tokens;this.imageStatus=`生成完成${usage!=null?` · Token ${this.number(usage)}`:' · 服务商未返回 Token 用量'}`;}else if(state.state==='failed'){this.imageBusy=false;this.imageStatus='生成失败：'+state.error;}else setTimeout(poll,650);}catch(error){this.imageBusy=false;this.imageStatus='读取结果失败：'+error.message;}};setTimeout(poll,500); }
      catch(error){this.imageBusy=false;this.imageStatus='生成失败：'+error.message;}
    }
  }
});
workbenchApp.config.errorHandler=(error,instance)=>{console.error(error);if(instance)instance.status=`界面操作失败：${error?.message||error}`;};
window.addEventListener('unhandledrejection',event=>{console.error(event.reason);const root=document.querySelector('#app');if(root)root.dataset.lastError=String(event.reason?.message||event.reason||'unknown');});
workbenchApp.mount('#app');

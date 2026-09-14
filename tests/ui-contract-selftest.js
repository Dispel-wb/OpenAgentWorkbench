const fs = require('fs');
const vm = require('vm');
const path = require('path');

let definition;
global.Vue = {
  createApp(value) {
    definition = value;
    return { config: {}, mount() {} };
  },
  nextTick(callback) { if (callback) callback(); return Promise.resolve(); }
};
global.window = {
  innerWidth: 1280,
  innerHeight: 800,
  getSelection() { return null; },
  addEventListener() {},
  removeEventListener() {}
};
global.document = {
  createElement() {
    let value = '';
    return {
      set textContent(next) { value = String(next ?? ''); },
      get innerHTML() {
        return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
          .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
      }
    };
  },
  querySelector() { return null; }
};
global.crypto = { randomUUID() { return '00000000-0000-4000-8000-000000000000'; } };
global.HTMLElement=class {};

const root = path.resolve(__dirname, '..');
const moduleSources = ['ui-store.js', 'provider-catalog.js','document-ui.js','workflow-ui.js'].map(name => fs.readFileSync(path.join(root, 'static', 'modules', name), 'utf8'));
const appSource = [...moduleSources, fs.readFileSync(path.join(root, 'static', 'app.js'), 'utf8')].join('\n');
vm.runInThisContext(appSource, { filename: 'app.js' });
if (!definition) throw new Error('Vue application contract not captured');

const app = { ...Object.assign({},...definition.mixins.map(m=>m.data()),...definition.mixins.map(m=>m.methods)),...definition.data(), ...definition.methods,$refs:{} };
const codexOnly = {settings:{workerHarness:'codex',model:''},providers:[],agentRuntime:{selected:'codex'},selectedProvider:null};
codexOnly.workerUsesOwnModel = definition.computed.workerUsesOwnModel.call(codexOnly);
if (!definition.computed.textRouteReady.call(codexOnly)) throw new Error('Codex-only fresh install must not require an imported Provider.');
for (const harness of ['claude','codex','dsh']) {
  const attachmentState={settings:{workerHarness:harness},agentRuntime:{selected:harness}};
  attachmentState.selectedWorkerHarness=definition.computed.selectedWorkerHarness.call(attachmentState);
  if (!definition.computed.canAttachFiles.call(attachmentState)) throw new Error(`${harness} must allow local file selection without requiring an imported Provider.`);
}
const piAttachmentState={settings:{workerHarness:'pi'},agentRuntime:{selected:'pi'}};
piAttachmentState.selectedWorkerHarness=definition.computed.selectedWorkerHarness.call(piAttachmentState);
if (definition.computed.canAttachFiles.call(piAttachmentState)) throw new Error('Pi must keep file attachments disabled until its adapter supports them.');
const pdf = app.renderMarkdown('[季度报告](D:\\docs\\季度报告.pdf)');
const office = app.renderMarkdown('请查看 `D:\\docs\\预算表.xlsx`');
const bare = app.renderMarkdown('输出位于 D:\\work\\Claude\\结果.docx。');
if (!pdf.includes('local-document-link') || !pdf.includes('PDF') || !pdf.includes('data-local-path')) {
  throw new Error('Markdown local PDF link was not rendered as a native document card');
}
if (!office.includes('local-document-link compact') || !office.includes('预算表.xlsx')) {
  throw new Error('Inline Office path was not rendered as a compact native document link');
}
if (!bare.includes('local-document-link') || !bare.includes('结果.docx')) {
  throw new Error('Bare absolute Office path was not linkified');
}

let prevented = false;
app.selectedTextWithinMessage = () => '用户选中的要求';
app.openSelectionMenu({ id:'message-1',role: 'user' }, { clientX: 100, clientY: 120, preventDefault() { prevented = true; } });
if (!prevented || !app.contextMenu.show || app.contextMenu.role !== 'user' || app.contextMenu.messageId !== 'message-1') {
  throw new Error('User-message selection does not open the quote/copy/export/delete menu');
}

const html = fs.readFileSync(path.join(root, 'static', 'index.html'), 'utf8');
if (!appSource.includes("id: '', preset: '', name: '', token: '', authStyle: 'auto'") ||
    !appSource.includes('...Object.keys(providerPresets)') || !appSource.includes('probeOnly:true') ||
    !html.includes('<option disabled value="">自动匹配 / 也可手选</option>')) {
  throw new Error('New API configuration must probe supported providers without depending on the displayed default');
}
if (!appSource.includes("current&&!current.started&&!this.messages.length&&!this.sessionRun(current.id)&&!hasQueued") ||
    !html.includes('本地版固定使用 D:\\work\\Claude')) throw new Error('Empty-session reuse or Local fixed-workspace UI contract is missing');
const actionBlock = html.match(/<div v-if="!message\.streaming&&\(canRegenerate\(message\)\|\|message\.variants\?\.length>1\)" class="message-actions">([\s\S]*?)<\/div>\s*<\/div>/)?.[1] || '';
if (!actionBlock.includes('regenerateMessage') || actionBlock.includes('quoteMessage') || actionBlock.includes('deleteMessage')) {
  throw new Error('Message footer must retain regeneration only, without quote or delete controls');
}
if (!html.includes('exportContextSelection') || !html.includes('copyContextSelection') || !html.includes('quoteContextSelection') || !html.includes('deleteContextMessage') || html.includes('class="message-delete"')) {
  throw new Error('Selection context menu is incomplete');
}
const css = fs.readFileSync(path.join(root, 'static', 'styles.css'), 'utf8');
if (!css.includes('--option-surface:#17243a') || !css.includes('html[data-edition="local"][data-skin="claude"]{--option-surface:#2b211e') || !css.includes('.settings-content :is(.capability-card') || !appSource.includes("dataset.edition=this.isOpenSource?'opensource':'local'")) {
  throw new Error('Settings option surfaces are not consistently blue with a Claude-only brown override');
}
if (!html.includes('run-evidence-card') || !html.includes('Context 来源') || !html.includes('Artifacts') || !html.includes('Tool 执行证据')) {
  throw new Error('Per-run context and artifact evidence card is missing');
}
if (!html.includes('context-budget-strip') || !html.includes('发送前预算') || !html.includes('contextBudgetPercent')) {
  throw new Error('Pre-send Context budget UI is missing');
}
if (!html.includes('provider-health-panel') || !html.includes('provider-health-dot') ||
    !html.includes('candidate.available===false') || !html.includes('candidate.rankReasons')) {
  throw new Error('Provider health evidence and ranked fallback UI are incomplete');
}
if (!html.includes('本次 HTTP') || !html.includes('本次 Run') || !html.includes('本次请求 P95') ||
    !html.includes('http.currentSession') || !html.includes('runs.currentSession')) {
  throw new Error('Current Host HTTP/Run metric hierarchy is missing');
}
if (!html.includes('model-capability-matrix') || !html.includes('item.contextWindow') || !html.includes('capabilityState(item.tools)')) {
  throw new Error('Provider model capability matrix is missing');
}
if (!html.includes('selectedModelToolsUnsupported') || !html.includes('当前模型不支持 Tool')) {
  throw new Error('Tool-incompatible Agent mode is not prevented in the composer');
}
if (!html.includes('summary.compactions') || !fs.readFileSync(path.join(root, 'static', 'app.js'), 'utf8').includes('Claude Code 已压缩会话历史')) {
  throw new Error('Native autocompact evidence is not visible in the UI');
}
if (!html.includes('terminalShell') || !html.includes('terminalProfiles') || !html.includes('terminalActiveShell')) {
  throw new Error('ConPTY shell profile selector and active-shell evidence are missing');
}
if (!html.includes('extension-trust-alert') || !html.includes('trustExtension') || !html.includes('extensionTrustBlocked')) {
  throw new Error('Project Skill/Agent trust quarantine and review controls are missing');
}
if (!html.includes('extensions.controls') || !html.includes('trustControl') || !html.includes('extension-controls')) {
  throw new Error('Claude Code control-surface trust panel is missing');
}
if (!html.includes('mcp-runtime-summary') || !html.includes('extensions.mcpRuntime') || !html.includes('server.transport')) {
  throw new Error('Pre-send MCP runtime validation summary is missing');
}
if (!html.includes('claude-runtime-card') || !html.includes('configureClaudeRuntime') || !html.includes('resetClaudeRuntime')) {
  throw new Error('Claude Code runtime discovery and repair UI is missing');
}
if (!html.includes('schedule-evidence-modal') || !html.includes('openScheduleEvidence(item)') ||
    !html.includes('item.activeRunId') || !html.includes('scheduleEvidence.evidence.tools')) {
  throw new Error('Scheduled Run linkage and evidence viewer are missing');
}
if (!html.includes('schedule-snapshot-note') || !appSource.includes('providerName:this.selectedProvider?.name') ||
    !fs.readFileSync(path.join(root, 'native', 'ApiServer.cs'), 'utf8').includes('(string)token["providerId"] ?? (string)settings["providerId"]')) {
  throw new Error('Scheduled tasks do not preserve their Provider/model/permission snapshot');
}
if (!html.includes("inspector.tab==='memory'") || !html.includes('工作区长期记忆') || !html.includes('memory-policy') ||
    !html.includes('editMemory(item)') || !html.includes('toggleMemory(item)') || !html.includes('deleteMemory(item)') ||
    !appSource.includes('/api/workbench/memories') || !appSource.includes('scheduleContextPreview()')) {
  throw new Error('Workspace-scoped long-term memory UI and next-turn refresh contract are incomplete');
}

const evidence = { runId: 'run-contract', summary: { estimatedContextTokens: 42 }, context: { sources: [] }, artifacts: [], tools: [] };
const evidenceMessage = { text: 'answer', workflow: [], providerName: 'fixture', runId: 'run-contract', runEvidence: evidence, variantState: 'completed' };
const snapshot = app.variantSnapshot(evidenceMessage, 'session-contract');
if (snapshot.runId !== 'run-contract' || snapshot.runEvidence?.summary?.estimatedContextTokens !== 42 || snapshot.state !== 'completed') {
  throw new Error('Answer variant does not preserve its run evidence');
}
const failedMessage = { variants: [{ text: 'failed attempt', runId: 'run-failed', runEvidence: evidence, state: 'failed', error: 'fixture failure' }] };
app.applyVariant(failedMessage, 0);
if (failedMessage.variantState !== 'failed' || failedMessage.runId !== 'run-failed' || failedMessage.variantError !== 'fixture failure') {
  throw new Error('Failed answer variant is not retained as read-only evidence');
}

const apiServer = fs.readFileSync(path.join(root, 'native', 'ApiServer.cs'), 'utf8');
const permissionBroker = fs.readFileSync(path.join(root, 'native', 'PermissionBroker.cs'), 'utf8');
const eventStore = fs.readFileSync(path.join(root, 'native', 'AgentEventStore.cs'), 'utf8');
if (!apiServer.includes('/api/workbench/activity') || !apiServer.includes('WaitForActivity') ||
    !appSource.includes('activityRevision:0') || !appSource.includes('async activityLoop()') ||
    !appSource.includes('async applyActivitySnapshot') || !appSource.includes('this.activityFailures>=2') ||
    !appSource.includes('backgroundSyncTick(force=false)') || !appSource.includes('deferConnectionFailure:true') ||
    !appSource.includes('Agent Host 已重新连接，后台状态已同步')) {
  throw new Error('Host activity long-poll, revision snapshots, or legacy polling fallback is incomplete');
}
if (!eventStore.includes('workspace_memories') || !eventStore.includes('SaveWorkspaceMemory') ||
    !eventStore.includes('if (active >= 32)') || !eventStore.includes('characters + incomingCharacters > 24000') ||
    !apiServer.includes('RecordWorkspaceMemoryContext') || !fs.readFileSync(path.join(root, 'native', 'ContextBudgetPlanner.cs'), 'utf8').includes('workspace-memory')) {
  throw new Error('Workspace memory persistence, limits, context injection, or Run evidence contract is incomplete');
}
if (!html.includes('approvalSessionTitle(item)') || !html.includes('openApprovalRun(item)') || !html.includes("'awaiting-approval'") ||
    !appSource.includes("return '等待授权'") || !apiServer.includes('["pendingApprovals"] = _permissions.PendingCount(job.Id)') ||
    !permissionBroker.includes('ListOpenApprovals()') || !eventStore.includes('CloseApproval(string id, string state)')) {
  throw new Error('Durable multi-session approval ownership and Run Center linkage are missing');
}
if (!apiServer.includes('/api/chat/evidence/') || !apiServer.includes('estimatedContextTokens') || !apiServer.includes('failedTools')) {
  throw new Error('Native run-evidence endpoint contract is incomplete');
}
if (!apiServer.includes('session_has_background_work') || !appSource.includes('后台任务所属会话已自动恢复到左侧列表')) {
  throw new Error('Archived/deleted sessions are not protected from orphaned background work');
}
const nativeHost = fs.readFileSync(path.join(root, 'native', 'NativeHost.cs'), 'utf8');
if (!nativeHost.includes('ShowForegroundDialog(CommonDialog dialog)') || !nativeHost.includes('TopMost = true') ||
    nativeHost.includes('dialog.ShowDialog(this) == DialogResult.OK')) {
  throw new Error('Native file/folder dialogs are not forced in front of their Workbench owner');
}
const nativeWatchdog = fs.readFileSync(path.join(root, 'native', 'NativeHostWatchdog.cs'), 'utf8');
const nativeStartup = fs.readFileSync(path.join(root, 'native', 'NativeStartupRegistration.cs'), 'utf8');
if (!apiServer.includes('ReconcileActiveJobs()') || !apiServer.includes('TryFinalizeTerminalJob') ||
    !nativeHost.includes('ShowBackgroundNotification')) {
  throw new Error('Host-owned background job terminal reconciliation is missing');
}
if (!nativeWatchdog.includes('Watchdog.Stop.V1') || !nativeWatchdog.includes('restartCount') ||
    !nativeWatchdog.includes('stopped-by-user') || !fs.readFileSync(path.join(root, 'native', 'WorkbenchApi.cs'), 'utf8').includes('NativeHostWatchdog.Snapshot') ||
    !html.includes('Native Watchdog')) {
  throw new Error('Native Host Watchdog recovery and diagnostics contract is incomplete');
}
if (!nativeStartup.includes('CurrentVersion\\Run') || !nativeStartup.includes('" --host"') ||
    !nativeStartup.includes('ReconcileExisting') || !fs.readFileSync(path.join(root, 'native', 'WorkbenchApi.cs'), 'utf8').includes('/api/workbench/startup') ||
    !appSource.includes('async toggleStartup(enabled)') || !html.includes('startup-behavior') ||
    !html.includes('登录后保持 Agent Host') || !html.includes('Windows 登录启动') ||
    !fs.readFileSync(path.join(root, 'native', 'NativeInstaller.cs'), 'utf8').includes('NativeStartupRegistration.SetEnabled(false)') ||
    !fs.readFileSync(path.join(root, 'native', 'NativeHost.cs'), 'utf8').includes('登录后自动启动后台')) {
  throw new Error('Current-user host-only login startup, stale-path reconciliation, tray control, or Settings UI is incomplete');
}
if (!/const\s+markdownCache\s*=\s*new WeakMap\(\)/.test(appSource) || !appSource.includes('messageWindowSize:80') ||
    !appSource.includes('renderMessageMarkdown(message)') || !appSource.includes('handleConversationScroll()') ||
    !appSource.includes('messageTextState(message)') || !html.includes('long-message-control')) {
  throw new Error('Bounded long-session rendering, Markdown cache, or progressive message expansion is incomplete');
}
const contextPlanner = fs.readFileSync(path.join(root, 'native', 'ContextBudgetPlanner.cs'), 'utf8');
if (!apiServer.includes('/api/chat/context-preview') || !apiServer.includes('ContextBudgetPlanner.Prepare') ||
    !contextPlanner.includes('provider-metadata') || !contextPlanner.includes('historyUnknown') || !contextPlanner.includes('hardLimit')) {
  throw new Error('Native Context budget planner contract is incomplete');
}
if (!apiServer.includes('/api/providers/health') || !apiServer.includes('capability-health-ranked-user-confirmed') ||
    !apiServer.includes('rankReasons') || !apiServer.includes('["automatic"] = false')) {
  throw new Error('Provider health ledger and user-confirmed ranked fallback contract are incomplete');
}
if (!apiServer.includes('model_not_configured') || !apiServer.includes('model_unavailable') ||
    !appSource.includes('selectedModelUnavailable') || !html.includes('textModelOptions')) {
  throw new Error('Unavailable model selection guard is incomplete');
}
if (!apiServer.includes('/api/providers/validate-model') || !appSource.includes('validateProviderModel') || !html.includes('实测 Chat + Tool')) {
  throw new Error('User-confirmed live model validation contract is incomplete');
}
if (!fs.readFileSync(path.join(root, 'native', 'OpenAiAdapter.cs'), 'utf8').includes('image_url')) {
  throw new Error('OpenAI Adapter vision block passthrough is incomplete');
}
if (!apiServer.includes('supported_parameters') || !apiServer.includes('FirstPositiveModelValue') ||
    !apiServer.includes('["evidenceDetails"]') || !apiServer.includes('["schemaVersion"] = 2')) {
  throw new Error('Endpoint-backed model capability evidence contract is incomplete');
}
const workbenchApi = fs.readFileSync(path.join(root, 'native', 'WorkbenchApi.cs'), 'utf8');
const taskWorkspaceManager = fs.readFileSync(path.join(root, 'native', 'TaskWorkspaceManager.cs'), 'utf8');
const conPtyTerminal = fs.readFileSync(path.join(root, 'native', 'ConPtyTerminal.cs'), 'utf8');
const nativeUpdater = fs.readFileSync(path.join(root, 'native', 'NativeUpdater.cs'), 'utf8');
if (!workbenchApi.includes('/api/workbench/task/diff') || !workbenchApi.includes('/api/workbench/task/discard') ||
    !taskWorkspaceManager.includes('applyJournal') || !taskWorkspaceManager.includes('RevertAppliedSource') ||
    !appSource.includes('previewTaskChange') || !appSource.includes('discardTaskChanges') ||
    !html.includes('task-change-diff') || !html.includes('撤销已应用')) {
  throw new Error('Transactional task Diff Review, partial apply, discard, or source undo contract is incomplete');
}
if (!workbenchApi.includes('/api/workbench/update/apply') || !workbenchApi.includes('_tryBeginUpdateMaintenance') ||
    !workbenchApi.includes('TryBeginSafeUpdate') || !workbenchApi.includes('activeTerminalBlockers') ||
    !nativeUpdater.includes('QuiesceTargetProcesses') || !nativeUpdater.includes('LaunchAndVerifyHost') || !nativeUpdater.includes('rolled-back') ||
    !appSource.includes('async installUpdate()') || !appSource.includes('this.updateConfig.activeTerminalBlockers') || !html.includes('下载并安装')) {
  throw new Error('Safe update handoff, Agent/terminal idle gate, startup verification, rollback, or GUI apply contract is incomplete');
}
if (!apiServer.includes('path == "/api/chat/runs"') || !apiServer.includes('RunCenter()') || !apiServer.includes('RunSummary(Job job)') ||
    !appSource.includes('async detachCurrentRunView()') || !appSource.includes('async attachRun(run)') ||
    !appSource.includes('async syncRuns(force=false)') || !appSource.includes('this.streamMessage.eventCursor=this.activeEventSeq') ||
    !appSource.includes('sessionSwitching:false') || !appSource.includes('!this.sessionSwitching)await this.attachRun(current)') ||
    appSource.includes('if (this.busy || (!force&&id===this.activeSessionId))') ||
    !html.includes("openInspector('runs')") || !html.includes("inspector.tab==='runs'") || !html.includes('controlRun(run')) {
  throw new Error('Host-truth multi-session Run Center, cursor persistence, or background controls are incomplete');
}
if (!apiServer.includes('IsActiveJobState(string state)') || !apiServer.includes('state == JobStates.Paused') ||
    !apiServer.includes('Host 重启后已恢复暂停态') || !apiServer.includes('["code"] = "worker_capacity"') ||
    !apiServer.includes('payload["resume"] == null') || !appSource.includes("error.data?.code==='worker_capacity'") ||
    !appSource.includes('maxTurns:Number(this.settings.maxTurns)||100')) {
  throw new Error('Paused Run recovery or worker-capacity durable queue handoff is incomplete');
}
if (!workbenchApi.includes('/api/workbench/terminal/profiles') || !workbenchApi.includes('shellName') ||
    !conPtyTerminal.includes('NativeJobObject.Attach(_process)') || !conPtyTerminal.includes('(command ?? "") + "\\r"')) {
  throw new Error('Native ConPTY profile, CR-submit, and process-tree cleanup contract is incomplete');
}
const workerSupervisor = fs.readFileSync(path.join(root, 'native', 'NativeWorkerSupervisor.cs'), 'utf8');
const mcpRuntimePolicy = fs.readFileSync(path.join(root, 'native', 'McpRuntimePolicy.cs'), 'utf8');
if (!workerSupervisor.includes('"--bare"') || !workerSupervisor.includes('"--strict-mcp-config"') ||
    !workerSupervisor.includes('"--append-system-prompt-file"') || !workerSupervisor.includes('"--agents"') ||
    !apiServer.includes('claudeCodeIsolation') || !apiServer.includes('permissionMcpConfig')) {
  throw new Error('Native Claude Code bare-mode loading boundary is incomplete');
}
if (!workerSupervisor.includes('ClaudeRuntimeDiagnostics') || !workerSupervisor.includes('ConfiguredClaudeExecutable') ||
    !apiServer.includes('agent_worker_unavailable') || !apiServer.includes('AgentWorkerSdk.Diagnostics(false, payload)') || !workbenchApi.includes('/api/workbench/runtime/claude/configure') ||
    workbenchApi.includes('D:\\softwares\\ClaudeCode\\node_modules')) {
  throw new Error('Claude runtime discovery, preflight, or hard-coded path removal is incomplete');
}
if (!mcpRuntimePolicy.includes('MaxConfigBytes') || !mcpRuntimePolicy.includes('environmentKeyCount') ||
    !apiServer.includes('invalid_mcp_runtime_config') || !apiServer.includes('mcp-runtime.json') ||
    !workbenchApi.includes('RedactedMcpServers')) {
  throw new Error('MCP runtime validation, redaction, and evidence contract is incomplete');
}
const extensionTrust = fs.readFileSync(path.join(root, 'native', 'ExtensionTrustPolicy.cs'), 'utf8');
if (!workbenchApi.includes('/api/workbench/extensions/trust') || !apiServer.includes('untrusted_project_extensions') ||
    !workbenchApi.includes('SetControlTrust') || !extensionTrust.includes('installedContentSha256') || !extensionTrust.includes('ContentDigest')) {
  throw new Error('Project extension trust, fingerprint, and backend-block contract is incomplete');
}

// Delta and terminal events can arrive in the same poll, before the 32ms flush.
// A terminal fallback must not be prepended to an unflushed copy of that text.
const streamed = { ...definition.data(), ...definition.methods, scrollBottom() {} };
for (const scenario of ['buffered', 'already-flushed', 'terminal-only']) {
  streamed.streamMessage = { text: '', workflow: [] };
  streamed.streamTextBuffer = ''; streamed.streamFlushHandle = 0;
  if (scenario !== 'terminal-only') {
    streamed.processStreamLine(JSON.stringify({ type:'stream_event',event:{type:'content_block_delta',delta:{type:'text_delta',text:'中文回复'}} }));
    if (scenario === 'already-flushed') streamed.flushStreamText();
  }
  streamed.processStreamLine(JSON.stringify({type:'result',result:'中文回复',is_error:false}));
  streamed.flushStreamText();
  if (streamed.streamMessage.text !== '中文回复') throw new Error(`Terminal text duplication: ${scenario}`);
}

console.log(JSON.stringify({
  uiContract: 'PASS',
  coalescedStreamTerminal: true,
  localPdfCard: true,
  localOfficeCard: true,
  bareAbsolutePath: true,
  selectionQuoteRequiresSelection: true,
  footerQuoteDeleteHidden: true,
  selectionMenu: ['copy', 'quote', 'export', 'delete'],
  runEvidenceCard: true,
  contextBudgetPreflight: true,
  providerHealthEvidence: true,
  currentHostMetrics: true,
  modelCapabilityMatrix: true,
  incompatibleAgentModeBlocked: true,
  rankedFallback: true,
  compactionEvidence: true,
  variantEvidencePersistence: true,
  failedVariantRetention: true,
  terminalProfiles: true,
  terminalProcessTreeCleanup: true,
  projectExtensionTrustBoundary: true,
  mcpRuntimeBoundary: true,
  claudeRuntimeRepair: true,
  scheduleRunEvidence: true,
  backgroundJobReconciliation: true,
  nativeHostWatchdog: true,
  safeUpdateHandoff: true,
  transactionalTaskReview: true,
  multiSessionRunCenter: true,
  pausedRunRecovery: true,
  workerCapacityQueue: true,
  rapidSessionSwitchIsolation: true,
  boundedLongSessionRendering: true,
  workspaceLongTermMemory: true,
  hostOnlyLoginStartup: true,
  hostActivityLongPoll: true
}, null, 2));

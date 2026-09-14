const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const assert = require('node:assert/strict');
let definition;
global.Vue = { createApp(value) { definition = value; return { config: {}, mount() {} }; }, nextTick(fn) { if (fn) fn(); return Promise.resolve(); } };
global.window = { addEventListener() {}, removeEventListener() {}, getSelection() { return null; } };
global.document = { documentElement: { dataset: {} }, querySelector() { return null; }, createElement() { return {}; } };
global.localStorage = { getItem() { return null; }, removeItem() {} };
for (const name of ['ui-store', 'provider-catalog', 'document-ui', 'workflow-ui'])
  vm.runInThisContext(fs.readFileSync(path.join(__dirname, '../static/modules', name + '.js'), 'utf8'));
vm.runInThisContext(fs.readFileSync(path.join(__dirname, '../static/app.js'), 'utf8'));
async function main() {
  const fixtures = [
    { name: 'fresh-no-D-drive', settings: {}, workspace: 'C:\\Users\\用户\\Documents\\OpenAgent', expected: 'C:\\Users\\用户\\Documents\\OpenAgent' },
    { name: 'empty-saved-workspace', settings: { workspace: '' }, workspace: 'C:\\中文 路径', expected: 'C:\\中文 路径' },
    { name: 'explicit-saved-choice', settings: { workspace: 'E:\\Existing' }, workspace: 'C:\\Host', expected: 'E:\\Existing' },
    { name: 'network-workspace', settings: {}, workspace: '\\\\fixture-server\\share', expected: '\\\\fixture-server\\share' }
  ];
  for (const fixture of fixtures) {
    const app = { ...definition.data(), ...definition.methods, customSkins: [], sortedSessions: [] };
    for (const name of ['loadSkins','applyAppearance','loadExtensions','loadAgentRuntimes','loadStartup','syncTranscripts','ensureModels','newSession','syncRuns']) app[name] = async () => {};
    app.api = async route => route === '/api/bootstrap' ? { edition:{id:'opensource',openSource:true,productName:'Open Agent'}, settings: fixture.settings, workspace: fixture.workspace, providers: [], sessions: [] } : route === '/api/workbench/update' ? {} : [];
    await app.bootstrap();
    assert.ok(!app.status.startsWith('启动失败'), app.status);
    assert.equal(app.workspace, fixture.expected, fixture.name);
    assert.equal(app.settings.workspace, fixture.expected, fixture.name + ': canonical persisted state');
  }
  const local = { ...definition.data(), ...definition.methods, customSkins: [], sortedSessions: [] };
  for (const name of ['loadSkins','applyAppearance','loadExtensions','loadAgentRuntimes','loadStartup','syncTranscripts','ensureModels','newSession','syncRuns']) local[name] = async () => {};
  local.api = async route => route === '/api/bootstrap' ? {
    edition:{id:'local',openSource:false,productName:'Claude Code 中文工作台'}, settings:{workspace:'C:\\old'}, workspace:'D:\\work\\Claude',
    providers:[],sessions:[{id:'legacy',workspace:'C:\\old',started:false}]
  } : route === '/api/workbench/update' ? {} : route === '/api/workbench/projects' ? [{path:'D:\\work\\Claude',name:'Claude'}] : [];
  await local.bootstrap();
  assert.equal(local.workspace, 'D:\\work\\Claude', 'Local canonical workspace');
  assert.equal(local.settings.workspace, 'D:\\work\\Claude', 'Local settings workspace');
  assert.equal(local.sessions[0].workspace, 'D:\\work\\Claude', 'Local legacy session workspace');
  console.log(JSON.stringify({ bootstrapWorkspace: 'PASS', cases: fixtures.length + 1 }));
}
main().catch(error => { console.error(error); process.exitCode = 1; });

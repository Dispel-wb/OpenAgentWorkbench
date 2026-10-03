const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');
const native = read('native/ShellPresentation.cs');
const api = read('native/ApiServer.cs');
const html = read('static/index.html');
const js = read('static/app.js');
const css = read('static/styles.css');
const origin = read('docs/FRONTEND_ORIGIN.md');

const checks = [
  [native.includes('open-agent-native-shell-v1'), 'native shell contract is missing'],
  [native.includes('independent-csharp'), 'independent C# implementation marker is missing'],
  [api.includes('ShellPresentation.Snapshot()'), 'bootstrap does not expose the native shell'],
  [html.includes('shell-search') && html.includes("nativeCommand('search')"), 'visible command entry is missing'],
  [html.includes('shell-context-menu') && html.includes("runShellAction('files')"), 'feature context menu is missing'],
  [html.includes('composer-controls') && html.includes('settings.permissionMode'), 'composer mode controls are missing'],
  [js.includes('applyNativePresentation()'), 'renderer does not consume native presentation'],
  [js.includes('openShellMenu(event') && js.includes('runShellAction(action)'), 'context-menu behavior is missing'],
  [css.includes('--native-nav') && css.includes('--native-panel'), 'native shell layout tokens are missing'],
  [css.includes('grid-template-rows:46px minmax(0,1fr)'), 'compact desktop topbar contract is missing'],
  [origin.includes('未复制、翻译或改写 PI-Desktop 的源代码'), 'independent implementation notice is missing']
];

for (const [ok, message] of checks) {
  if (!ok) throw new Error(message);
}

console.log('native shell presentation self-test passed');

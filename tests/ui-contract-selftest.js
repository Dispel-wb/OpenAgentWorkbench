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

const root = path.resolve(__dirname, '..');
vm.runInThisContext(fs.readFileSync(path.join(root, 'static', 'app.js'), 'utf8'), { filename: 'app.js' });
if (!definition) throw new Error('Vue application contract not captured');

const app = { ...definition.data(), ...definition.methods };
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
app.openSelectionMenu({ role: 'user' }, { clientX: 100, clientY: 120, preventDefault() { prevented = true; } });
if (!prevented || !app.contextMenu.show || app.contextMenu.role !== 'user') {
  throw new Error('User-message selection does not open the quote/copy/export menu');
}

const html = fs.readFileSync(path.join(root, 'static', 'index.html'), 'utf8');
const actionBlock = html.match(/<div v-if="!message\.streaming" class="message-actions">([\s\S]*?)<\/div>\s*<\/div>/)?.[1] || '';
if (!actionBlock.includes('quoteMessage') || actionBlock.includes('copyMessage') || actionBlock.includes('exportMessage')) {
  throw new Error('Message footer actions do not match the compact quote-only contract');
}
if (!html.includes('exportContextSelection') || !html.includes('copyContextSelection') || !html.includes('quoteContextSelection')) {
  throw new Error('Selection context menu is incomplete');
}

console.log(JSON.stringify({
  uiContract: 'PASS',
  localPdfCard: true,
  localOfficeCard: true,
  bareAbsolutePath: true,
  userMessageQuote: true,
  footerCopyExportHidden: true,
  selectionMenu: ['copy', 'quote', 'export']
}, null, 2));

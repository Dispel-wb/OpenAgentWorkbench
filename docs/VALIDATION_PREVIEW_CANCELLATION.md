# 文档预览取消修复 — 2026-09-10

状态：源代码与候选构建通过验证，未覆盖 D:\softwares\ClaudeCode 的已安装 6.4.24；不是新版本发布。

## 行为合同

- 关闭、替换预览或销毁界面时，取消旧文档元数据请求与 PDF 下载。
- 旧请求不得覆盖新预览；关闭后不保留加载状态。
- 调用方取消不改变全局连接状态；真实网络错误和超时保留原处理。
- 清理超时与调用方 abort 监听器；不改用户数据格式。

## 验证

- `node tests/ui-connection-state-selftest.js`：通过原有连接恢复/延迟错误/参数保留断言及 6 个取消场景（调用方取消、预先取消、元数据关闭、替换、PDF 正文下载中关闭、组件销毁）。取消场景采用可中断的 fetch 夹具，不等于真实网络测量。
- `node tests/ui-contract-selftest.js`：通过。
- Local EXE 构建通过，候选路径 `dist/preview-cancellation/ClaudeCodeWorkbench.exe`。
- SHA-256：`4CEC8D5323506ACAD537B12DF1F743D984BE3576C1FD5D64D4A7E84E57808D3D`。
- `document-preview-ui.ps1` 对候选 EXE 的真实 Host + Chrome 测试通过：文档布局、沙箱、焦点、可访问对话框、工作流编辑器。受限运行首次未启动 Host，经允许提升运行权限后通过。此测试不替代 NVDA 或原生 WebView2 人工验收。

此次使用 build-lean-frontends 的小范围修复和回归约束，未扩大为界面重设计、部署或公开发布。没有调用付费模型。其余成熟度缺口仍见 `VALIDATION_6.4.24.md`。

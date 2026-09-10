# 6.4.24 本地验收（2026-09-03）

本轮按用户明确授权调用付费模型，先验证、修复，再补功能。不是“七项成熟度缺口全部完成”的声明，也未发布公共仓库。

候选 EXE：`dist/next/ClaudeCodeWorkbench.exe`；独立同环境复构建 `dist/repro-6.4.24/ClaudeCodeWorkbench.exe`。
两次 SHA-256 均为 `49FED884D2BACDD3F603F423876ADEA4D3CE6922487FA4A056A6F4E3F29468E3`。

已于 2026-09-03 13:32（Asia/Shanghai）安装到 `D:/softwares/ClaudeCode/ClaudeCodeWorkbench.exe`，安装后校验哈希一致。安装时无正式版进程在运行，未强关任务、未自动启动或重放会话；保留 6.4.23 previous 备份。

## 复现并修复的问题

1. **权限 MCP 未加载**：内部生成的 `env.CLAUDE_GUI_PERMISSION_TIMEOUT_SECONDS` 是数字，实际 CLI 的 MCP 配置要求字符串。已改成字符串；外部 MCP 的非字符串 env/args 在发起模型调用前拒绝。原真实付费请求已发出 Read，随后因权限工具不可用失败。
2. **限定工具绕过目录权限**：把 scoped 工具同时传给 `--allowed-tools`，使 CLI 自动批准 Edit，绕过工作台的逐次路径检查。旧候选在临时目录中被真实 CLI 成功写出工作区，证明不是静态推测。现在 `--tools` 只管理可见工具，只有权限 MCP 本身自动批准；文件操作经过持久化权限规则。真实核心回归验证工作区内读/写成功、越界写入拒绝。
3. **聊天标题副请求干扰任务**：真实核心会额外发送标题生成请求，即使使用 bare。原适配器将同一 Run URL 上的标题错误升级为当前任务失败。通过官方 `CLAUDE_CODE_DISABLE_TERMINAL_TITLE=1` 关闭工作台不需要的重复标题请求，避免额外费用及这条已复现的失败路径；没有伪造标题模型响应或忽略主请求错误。
4. **本地错误污染 API 健康状态**：已识别的权限 MCP/核心缺失错误归类为 `local_runtime`，不记入 Provider 失败/冷却，不建议切换模型。401、429、上游断流等原有分类继续验证。

官方依据：[环境变量](https://code.claude.com/docs/en/env-vars)、[bare 程序化运行](https://code.claude.com/docs/en/headless)。本机 Claude 2.1.226 的 bare 内置工具主要是 Bash、Read、Edit，不能把 `Write` 可见性当作保证。实际创建文件用 Read + Edit（空 old_string），不扩大权限。

## 本轮新增功能

DOCX/PPTX 内嵌 PNG/JPEG 图片预览，以及 DOCX 显式分页符提示。图片保留在只读沙箱文档内，不执行宏、SVG、脚本，不加载外部关系或本地任意路径。

- OOXML 关系仅解析到压缩包内；允许合法 `../media`，拒绝跳出包、URL、UNC 和反斜杠路径。
- PNG/JPEG 文件头和尺寸校验：最大 8192 × 8192 且不超过 1600 万像素；每图 1 MB，每份 DOCX / 每页 PPTX 最多 100 张、3 MB，总输出另有 6 MB 上限。
- 格式不支持或超限时显示占位说明。复杂图表、精确分页、幻灯片原始几何布局、Office 编辑仍不支持，不能称作完整 Office 排版引擎。

## 真实付费验收

只使用用户已保存的 SiliconFlow 配置，密钥仍用 DPAPI；复制到隔离临时数据目录，结束清除测试凭据、会话和文件，没有修改正式聊天和默认模型。每个 Worker 10 个内部回合上限，输出 1024 Token 上限，付费验收关闭 CLI 请求重试，单轮 100 秒到期停止；没有自动重放失败任务。

| 请求模型 | 实际通过的场景 | 最终证据 |
| --- | --- | --- |
| deepseek-ai/DeepSeek-V4-Flash | 中文聊天、工具协议、实际中文路径读写、同进程多轮记忆、两个父任务、依赖节点顺序与结果交接 | `dist/live-acceptance/acceptance-20260903-132802-981b1f.json`（最终二进制） |
| MiniMaxAI/MiniMax-M2.5 | 实际中文路径读写、第二轮从上下文复述随机文件标记 | `dist/live-acceptance/acceptance-20260903-132023-3451a5.json`（最终权限修复前的候选；最终版越界规则另有真实 CLI 离线回归） |

已返回用量合计：DeepSeek 19,418 输入 / 1,084 输出，MiniMax 4,042 输入 / 834 输出；共 **25,378 Token**。包括本轮已回传的部分失败场景和两项初始验证。第一次 MCP 失败及早期取消的节点没有完整回传，因此这是已知用量下限，**不是完整账单，未估算价格**。Codex / DSHarness 本轮协议回归为离线，未冒充付费云端核心验收。

## 回归证据

- `product-gaps-selftest.ps1`：46 项签名、权限元数据、文档、图片边界、DAG、中文 URL 断言。
- `claude-permission-core-integration.ps1`：真实 Claude CLI + 真实 Host/MCP，模型部分为本机确定性夹具；读写、续接、越界拒绝、DAG。不得用“无文件”代替读取真实工具返回及终态。
- `provider-health-integration.ps1`：本地错误隔离；认证/限流分类、降级建议过滤与用户确认、脱敏、重启持久化。
- `adapter-resilience-integration.ps1`：HEAD 无正文、4xx、首包/空闲超时、断流、取消后上游停止、中文、9 组协议形态。
- Native Worker、扩展信任、DAG、Codex Worker SDK、DSHarness SDK 回归通过。
- UI contract / connection-state / document-preview：通过；实际 PNG 解码、外部图片零请求、PDF blob、对话框与键盘焦点。已检查本机截图。
- 两次同环境构建字节一致，不等于已完成所有干净 Windows 组合的复现验收。
- KISS 检查：新增 OfficeMedia 无 high 项；项目仍有 39 个历史 high 复杂度提示，本轮未用无关重构扩大改动。

## 明确保留的成熟度缺口

- 真实 10 万字历史会话的原生 WebView2 FPS/内存/选区测量：还缺足量实际输入与原生测量；合成性能测试不替代它。
- NVDA/Narrator 的实际朗读与完整键盘人工验收，尤其 PDF/Office 阅读器内焦点。
- Codex / DSHarness 的审批互通、DSHarness 跨进程续接：受 CLI 支持范围约束，不静默绕过。
- 完整 Office 图表/图形/精确分页；扩展沙箱与全部 CLI 自有插件边界；跨机器/干净 Windows 矩阵。
- 后台 72 小时 soak 旧记录为失败（5388 个样本）。本轮只读取状态，没有重启测试、在对话等待或将其称为通过。

新增脚本 `tests/live-workbench-acceptance.ps1` 必须显式传 `-AllowPaid`。真实模型输出不确定，失败时保留脱敏摘要，停止后续场景，不能自动改成假数据再报通过。

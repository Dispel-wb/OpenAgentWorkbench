# Open Agent Workbench v1.0.0

这是首个正式稳定版，面向 Windows 10/11 x64。

## 主要内容

- Claude Code、Codex CLI、DSHarness CLI 与 Pi 四种核心切换。
- 原生 Host、任务队列、权限审批、终端、任务级工作区与恢复机制。
- 定时任务、插件与 Skill/MCP 入口、多服务商模型适配。
- 对话过程分层展开、最终结果统一展示、Markdown 与文件预览改进。
- 深浅色主题及 Claude、Codex、兼容三种界面模式。

## 稳定性验证

固定本地候选从 2026-09-30 15:35:06（UTC+8）运行至 2026-10-03 15:35:18（UTC+8）：

- 有效运行时间：259,211 秒
- 并行循环：831 轮
- Claude / Codex / DSHarness / Pi：各 831 次通过、0 次失败
- 总失败数：0
- 挂起间隙：0
- 候选程序和冻结输入在结束时复核 SHA-256 一致

这项测试使用四个真实 CLI 核心与本地确定性模型夹具，不等同于第三方云端服务认证。机器可读证据见 `docs/V1_0_0_SOAK_EVIDENCE.json`。

## 下载校验

`OpenAgentWorkbench-1.0.0.exe`

SHA-256：`98976505B98903F21C8678781751438C29507D891D7950FCA5F184446BBE6D41`

该 EXE 尚未进行商业代码签名，Windows 可能显示“未知发布者”。请只从本仓库 Release 下载并核对 SHA-256。

# Open Agent Workbench

Open Agent Workbench 是一个面向 Windows 10/11 的中文 Agent 桌面工作台。它把图形界面、长期运行的本地 Host、任务队列、权限审批、终端、Skill/MCP 入口与多服务商模型适配放在同一个原生 EXE 中。

> 当前公开版本是 **Preview**，用于代码审阅、试用和收集兼容性反馈，不代表正式版 1.0.0。程序尚未进行商业代码签名，请只从本仓库的 Releases 下载并核对 SHA-256。

## 已具备的核心能力

- UI 与后台任务生命周期分离：关闭窗口后 Host 和任务可以继续运行，由系统托盘控制。
- 原生 C# Worker Supervisor、SQLite WAL 事件存储、任务租约、故障恢复与幂等请求。
- 多服务商 API 预设、接口探测、模型读取，以及文字/生图能力分流。
- ConPTY 终端、文件与命令权限审批、任务级工作区、检查点与回滚基础能力。
- 对话归档、恢复、删除、队列插入、引用、TXT 导出、文件卡片和本地路径跳转。
- 深浅色主题以及 PNG + CSS + JSON 皮肤包导入。
- 真正的 Claude Code / Codex CLI / DSHarness CLI 核心切换；认证、停止和能力边界详见 [命令行核心说明](docs/AGENT_WORKER_SDK.md)。

## Preview 的边界

- 仅提供 Windows x64 构建；需要 Microsoft Edge WebView2 Runtime。
- 发布的 EXE 未签名，Windows 可能显示未知发布者提示。
- Office/PDF 当前以原生文件卡片和系统默认应用打开为主，并非完整的内嵌文档编辑器。
- 服务商兼容性依赖其 OpenAI/Anthropic 兼容程度；不同中转服务的流式字段仍可能需要适配。
- 尚未完成正式版要求的 72 小时稳定性验证、干净 Windows 矩阵和证书签名流程。

## 从源代码构建

要求：

- Windows 10/11 x64、.NET Framework 4.8
- Python 3.13 与 Pillow 12.3.0（构建图标）
- PowerShell；Node.js 用于测试和可选 DSHarness

构建开源版：

```powershell
python -m pip install Pillow==12.3.0
.\restore_build_dependencies.ps1
.\build_exe.ps1 -Edition OpenSource -DependencyRoot .\.packages -PythonPath (Get-Command python).Source
```

构建依赖按锁定版本与 SHA-256 恢复，不强制安装 Visual Studio。输出位于 `dist\opensource\OpenAgentWorkbench.exe`。详见 [开发指南](docs/DEVELOPMENT.md)、[可复现构建](docs/BUILD_REPRODUCIBILITY.md)、[Windows 测试矩阵](docs/WINDOWS_TEST_MATRIX.md)。

## 数据与安全

- 默认工作区：`D:\work\OpenAgent`
- 默认安装目录：`D:\softwares\OpenAgentWorkbench`
- 无可用 D 盘时工作区回退到用户文档目录，安装目录回退到 LocalAppData/Programs；也可显式指定其他路径。
- Provider token 使用 Windows DPAPI CurrentUser 加密保存。
- 本地 API 绑定 `127.0.0.1`，并使用每次安装生成的本机认证密钥。
- 诊断包会尝试脱敏，但分享前仍应人工检查路径、机器名和任务摘要。

请勿提交真实 API Key、工作区文件、运行数据库、日志或诊断包。安全问题请通过 GitHub Security Advisory 私下报告。

## 品牌与项目关系

本项目不包含 Claude、Codex 或其他第三方产品的图像资产，也不隶属于或受 Anthropic、OpenAI、Microsoft 及任何模型服务商背书。相关名称仅用于说明兼容协议或用户可选的外部服务。

## 许可证

项目自有代码采用 [MIT License](LICENSE)。随源码分发的第三方组件适用各自许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

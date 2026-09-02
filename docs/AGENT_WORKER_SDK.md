# 命令行核心接入

工作台启动真实 Agent CLI，不把模型 API 适配器当作 Agent 核心。UI、任务队列、事件持久化和进程管理共用；推理循环、内置工具与核心策略由所选 CLI 执行。

## 选择与安装

入口：**设置 → 工作区 → 命令行核心**。自动模式优先 Claude Code，找不到才回退 Codex；DSHarness 必须显式选择。切换不改变已有 CLI 的登录状态。

| 核心 | 启动方式 | 认证与模型 |
| --- | --- | --- |
| Claude Code | 原生 CLI 的 stream-json | 工作台所选 Provider / 文字模型；必要时经本地协议适配器 |
| Codex CLI | `codex exec --json`，后续轮使用 `exec resume` | Codex 自身登录和配置；工作台模型输入可留空 |
| DSHarness CLI | `node …/dsh/lib/bin.js --profile sdk` 的 JSON-RPC | 工作台 Provider 的兼容文字接口、密钥和模型；通过子进程环境传递 |

Codex 自动查找设置中的路径、`CLAUDE_GUI_CODEX_EXE`、PATH、桌面安装的版本化 bin 目录。不需要安装 Claude Code。导入的 Chat Completions 接口不会被直接当作 Codex Responses 接口。

本轮真实测试的 Codex CLI 为 0.152.1。已信任的项目说明和启用的工作区记忆会作为带明确标签的用户上下文交给 Codex/DSHarness；不是伪装成核心 system prompt，也不会改变其权限策略。Skill 元数据和文件地址按需提供，不预载全部 Skill 正文。工作台的 Claude 专用 MCP/Agent 配置不会自动注入其他核心；它们使用各自的扩展配置。

DSHarness 使用官方 `@deepseek-ai/dsh`，当前验证版本 **0.1.2-alpha.5**。验证时 npm latest 的 0.1.1-rc.2 没有 SDK profile，不能用于此适配器。需要 Node.js >= 22.19。

```powershell
.\install_dsh_runtime.ps1 -InstallRoot 'C:\Apps\OpenAgentWorkbench'
```

运行时在指定安装目录的 `runtimes/dsh` 下独立安装。`npm ci` 使用提交的锁文件、禁用安装脚本；不会覆盖全局 Node、Codex 或 Claude。若运行时不在标准位置，可在设置中填写 `lib/bin.js` 和 `node.exe` 的绝对路径。

## 能力边界

| 能力 | Codex exec 适配器 | DSHarness SDK 适配器 |
| --- | --- | --- |
| 中文多轮对话 | 已离线验证 | 已离线验证 |
| 核心事件、工具结果、Token | 已映射；累计 Token 转为本轮差值 | 已映射；按本轮累计 |
| 连续会话 | 保存 thread ID 并 resume | 同一存活核心进程支持多轮 |
| 跨进程续接 | 依赖 Codex 保存的 thread | 尚未验证；核心丢失后拒绝自动重放输入，保留失败现场 |
| 停止 | Windows Job Object 结束独立任务进程树 | 同左；上游无单会话取消方法 |
| 运行中更改方向 | 不支持实时注入；优先指令排到下一轮 | 同左 |
| 工作台交互审批、工具黑白名单 | 暂不支持，运行前拒绝相应模式 | 暂不支持；额外授权请求安全停止 |
| 只读 / 工作区写入 / 完整权限 | 传给 Codex sandbox；保留核心策略拒绝 | 传给 DSH_PERMISSION_MODE |

`full` 是用户主动选择的危险模式，不能视为工作区沙箱。工作台的 Claude Permission MCP 不能自动约束其他核心；不支持的限制必须拒绝，不能忽略。CLI 自己配置的 MCP、hooks、plugins、skills 也属于用户信任边界。

DSHarness 遥测、会话日志上报和包清单上报插件在本适配器启动补丁中禁用；诊断日志仍保留本地。DSHarness 的额外权限审批响应方法、细粒度子 Agent 控制尚未接入，不能宣称与其所有原生功能完全等价。

## 开发与验证

内部 `claude-stream-json-v1` 是事件归一化协议名称，不代表 Codex/DSHarness 依赖 Claude CLI。`AgentWorkerSdk.cs` 负责发现和启动；`AgentWorkerBridge.cs` 处理 Codex exec JSONL；`DshWorkerBridge.cs` 处理 stdio JSON-RPC。

```powershell
.\tests\agent-worker-sdk-integration.ps1 -Executable .\dist\next\ClaudeCodeWorkbench.exe -DependencyRoot .\.packages
.\tests\dsh-worker-sdk-integration.ps1 -Executable .\dist\next\ClaudeCodeWorkbench.exe
node .\tests\codex-core-offline-smoke.js <workbench.exe> <codex.exe>
node .\tests\dsh-core-offline-smoke.js <workbench.exe> <dsh/lib/bin.js>
```

前两个使用协议夹具。后两个启动真实 CLI，隔离核心配置并连接本机模拟模型服务，不读取真实 API Key、不调用付费模型。DSHarness 真实 `read` 工具已经通过中文/空格路径文件测试；当前 Codex 测试的读取命令被核心策略拒绝，测试记录此拒绝，**不代表 Codex 命令执行已验证通过**。

参考：[Codex 非交互模式](https://developers.openai.com/codex/noninteractive)、[Codex CLI 参数](https://developers.openai.com/codex/cli/reference)、[DSHarness CLI](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/cli/README.md)、[DSHarness SDK](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/sdk/README.md)。上游 master 文档可能领先于 npm 包；以锁定版本和回归证据为准。

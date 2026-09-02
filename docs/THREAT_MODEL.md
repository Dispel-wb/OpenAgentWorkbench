# Claude Workbench 威胁模型

## 保护目标

- API Key、Host 本机认证密钥与权限审批密钥不能以明文落盘或进入诊断包。
- UI、Host、Worker 的进程边界不能绕过任务权限清单。
- 工作区外访问、删除、危险命令应受所选核心权限策略约束；不能宣称所有核心均接入工作台交互审批。
- ZIP 皮肤、附件路径、Provider 响应和 Claude JSONL 都按不可信输入处理。

## 信任边界

1. UI 只通过 127.0.0.1 的随机端口访问 Host，并同时提交 DPAPI 保护后注入的本机密钥及协议版本。
2. Host 是任务、队列、审批、调度和事件真源。关闭或崩溃 UI 不改变任务状态。
3. Worker 位于 Windows Job Object 中。Claude 使用任务级权限 MCP；Codex 使用自身 sandbox，DSHarness 使用自身权限模式。未适配的工作台交互审批/工具白名单在运行前拒绝，不能静默扩大权限。
4. Provider 与 MCP 属于外部系统；超时、错误正文和模型元数据均不可信。
5. 配置任务隔离时，修改在 Git worktree 或快照内完成，Review 后合并；直接工作区/full 模式不能保证所有变更可回滚。

## 已实施控制

- DPAPI CurrentUser 加密 Provider Key、Host Secret 和 Permission MCP Secret。
- SQLite WAL、幂等 requestId、事件 ACK、Worker 心跳/租约与故障恢复。
- 路径规范化、任务根目录、工作区外一次性审批、危险命令分类。
- ZIP 路径穿越阻止、解包大小/数量限制、皮肤 Schema 验证。
- 统一日志与诊断包脱敏；诊断包默认只保存在本机，不自动上报。
- 调度原子领取、租约恢复、指数退避和失败箱。

## 剩余风险

- 未取得代码签名证书前，Windows 无法验证发行者身份；发布包只能校验 SHA-256。
- full 权限是显式逃生舱，会绕过部分审批，不应作为默认模式。
- Provider 对模型能力的声明可能错误；视觉/生图能力应以实际探测证据为准。
- 用户批准任意命令后，命令本身仍可访问该用户账户能够访问的资源。
- CLI 自身配置、MCP、hooks 和 Skill 的信任边界不能仅靠工作台项目扩展扫描覆盖。
- DSHarness 当前为已锁定的 Alpha；跨进程续接、细粒度子 Agent 控制仍未验收。
- 本轮只完成针对性代码检查、离线安全回归与发布扫描；不是独立全面渗透测试。未完成项见 Windows 矩阵与发布流程文档。

## 发布门禁

- 扫描源码、日志夹具和诊断 ZIP，不得出现完整测试 Key。
- 100 次 Host/Worker 故障注入无已确认事件丢失。
- Stable 候选至少运行 24 小时 soak；成熟版必须完成 72 小时 soak。
- 发行 EXE、previous EXE 与发布清单中的版本和 SHA-256 必须一致。

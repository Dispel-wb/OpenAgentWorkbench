# 架构与模块责任

Windows x64 EXE 内嵌 Vue、CSS、JS、WebView2 SDK 和所需 .NET 程序集。UI 与 Host 独立进程，CLI Worker 是 Host 管理的子进程树。WebView2 Runtime 与可选 CLI 不属于 EXE 内嵌核心。

| 模块 | 主要文件 | 职责 |
| --- | --- | --- |
| Provider | ProviderStore.cs、OpenAiAdapter.cs、static/modules/provider-catalog.js | 密钥存储、服务商目录、接口/流式协议适配 |
| Update | NativeInstaller.cs、NativeUpdater.cs；ApiServer 中的协调入口 | 安装、摘要验证、替换、回退和更新协调 |
| Terminal | ConPtyTerminal.cs、ToolRuntimePolicy.cs | 终端与工具执行生命周期 |
| Task | NativeWorkerSupervisor.cs、AgentWorkerSdk.cs、DshWorkerBridge.cs、AgentEventStore.cs | Worker、任务状态、队列/事件与核心协议归一化 |
| Artifact | ArtifactStore.cs；WorkbenchApi 中的预览入口 | 制品记录和预览，SQLite 真源由 Event Store 统一连接管理 |
| UI Store | static/modules/ui-store.js、static/app.js | 界面状态、展示缓存、事件消费 |
| Host/UI 协调 | Program.cs、ApiServer.cs、WorkbenchApi.cs、NativeHost.cs | 本机认证路由、托盘与窗口生命周期 |

模块名描述责任，不等于已拆成独立程序集。Artifact Store 为 partial class，UI Store 和 Provider catalog 为独立前端模块；ApiServer、WorkbenchApi、app.js 仍然较大，需要持续按测试边界提取，不能宣称完全解耦。

任务状态以 Host 和 SQLite 为准，DOM 不拥有运行状态。核心输出归一化后写入事件存储，UI 从游标读取。断开窗口不自动取消任务；主动停止应结束对应 Job Object 的整个进程树。

依赖方向应保持 UI → 本机 API → 业务模块 → 持久化/CLI。新增核心优先实现已有归一化协议，不复制整个聊天服务。权限能力不能由 UI 单独决定，必须在启动前服务端校验。

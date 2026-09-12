# 开发指南

## 最小构建环境

Windows x64、.NET Framework 4.8、PowerShell、Python 3.13 + Pillow 12.3.0。Node.js 用于前端测试与可选 DSHarness，不是 C# Host 的运行依赖。构建所需 Roslyn、WebView2 SDK、Newtonsoft 从固定 NuGet 包恢复，不强制安装 Visual Studio。

```powershell
python -m pip install Pillow==12.3.0
.\restore_build_dependencies.ps1
.\build_exe.ps1 -Edition OpenSource -DependencyRoot .\.packages -PythonPath (Get-Command python).Source
```

发行采用 OpenSource 目标；Local 目标依赖私有图像，公共仓库不包含这些资源。不要为使 Local 构建通过而提交私有图像。

## 离线回归

```powershell
$exe = '.\dist\opensource\OpenAgentWorkbench.exe'
node .\tests\ui-contract-selftest.js
.\tests\agent-worker-sdk-integration.ps1 -Executable $exe -DependencyRoot .\.packages
.\tests\dsh-worker-sdk-integration.ps1 -Executable $exe
.\tests\event-store-selftest.ps1 -Executable $exe
.\tests\task-security-selftest.ps1 -Executable $exe
.\tests\edition-smoke.ps1 -Executable $exe -ExpectedEdition opensource -NodePath (Get-Command node).Source
```

集成测试应使用随机临时目录、独立 mutex、假的 Provider 与显式测试环境变量。不要复用日常数据目录、实际令牌或依赖真实 API 才能通过。清理前验证路径在测试根内。

新核心的真实 CLI 夹具见 [命令行核心](AGENT_WORKER_SDK.md)。UI source-contract 测试不是视觉验收；改布局后仍需要 WebView2 中的真实渲染检查。

## 修改约定

默认中文语境，技术名词不必强译。保持现有前后端边界，逐步提取大文件，不为单一功能建立通用框架。修改数据库增加版本迁移和旧数据回归，不手改用户数据库。

协议兼容问题保留脱敏错误证据。流式请求在可能已经产生输出或副作用后，不应透明重放；取消应传播到上游。测试失败不能通过隐藏错误、放宽权限或增加等待时长掩盖。

## 完整离线回归

恢复锁定依赖并构建 OpenSource 候选后，从项目根目录运行：

```powershell
.\tests\run-offline-regression.ps1 -Executable .\dist\candidate\OpenAgentWorkbench.exe -OutputDirectory .\dist\regression-new -DependencyRoot .\.packages
```

需 PowerShell 7、Node.js 和已恢复的 Pi runtime；测试使用锁定编译器，不依赖 Visual Studio Community 安装路径。输出目录必须是新目录，保留之前的失败证据。每个用例使用独立的 EXE 副本和进程，避免按程序路径清理时影响正式安装或长期测试。报告记录候选摘要、测试文件摘要、退出码、超时和日志；运行期间修改候选或已有测试文件会使整轮证据无效。

这套 70 项回归是离线验证，不替代原生键盘/读屏器、干净 Windows 矩阵、付费模型服务、独立安全复核或 72 小时稳定性验收。子进程不继承 Anthropic 令牌与地址形成的默认 Provider；各测试必须显式准备自己的离线配置。100 次故障恢复仍全部执行，并在较慢 CI 主机上单独允许至少 900 秒；Pi 资源稳定性用例执行 60 个循环、20 个循环预热并要求后续句柄增长不超过门槛，单独允许至少 600 秒。其余项使用所配置的超时。

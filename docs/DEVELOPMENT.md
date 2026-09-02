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

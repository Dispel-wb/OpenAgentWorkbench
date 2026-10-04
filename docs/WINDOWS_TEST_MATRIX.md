# Windows 验证矩阵

已建立可执行测试入口；有测试脚本不等于每种机器均已验收。1.0.0 候选已在当前 Windows 11 build 26300 / Evergreen / x64 上分别通过 Windows PowerShell 5.1 与 PowerShell 7，覆盖新数据启动、SQLite 完整性、中文与空格路径、非 D 盘和离线 API。测试路径使用编码无关的 Unicode 构造，避免无 BOM 脚本在 Windows PowerShell 5.1 中被 ANSI 代码页误解。

| 场景 | 验证方法 | 当前证据边界 |
| --- | --- | --- |
| Windows 10 / 11 | 校验实际 OS build | Win11 当前主机通过；干净 Win10 与干净 Win11 待运行 |
| Evergreen / Fixed WebView2 | 启动实际 UI，记录 Runtime | Evergreen 通过；不同 Fixed 版本待运行 |
| 无 Claude 安装 | RequireClaudeAbsent + CLI 核心测试 | 假核心独立启动通过；干净无 Claude 机器待验收 |
| 非 D 盘、中文空格路径 | 在 C 盘临时目录复制安装/工作区 | 本机路径通过；物理无 D 盘默认回退待实机验证 |
| 中文用户名 | RequireUnicodeUserProfile | 目录夹具不能替代真实账户，此项待运行 |
| 非管理员账户 | 检查 token 是否 elevated | 需干净 runner 实际验证 |
| 新系统/旧数据升级 | 临时数据、安装回滚、Event Store migration | 夹具通过；历史真实备份采样待扩充 |

```powershell
.\tests\windows-clean-matrix.ps1 -Executable .\dist\opensource\OpenAgentWorkbench.exe -ExpectedEdition opensource -ExpectedWindows windows-11 -ExpectedPrivilege non-admin -RequireNonDDrive -Full -DependencyRoot .\.packages -NodePath (Get-Command node).Source
```

`.github/workflows/windows-clean-matrix.yml` 仅手动启动，依赖带对应标签的隔离 self-hosted runner。维护者必须先提供机器/快照和 Fixed Runtime 路径；不要让不可信 PR 在个人电脑 runner 上执行。每行报告保存真实 OS、账户、Runtime、检查结果；不满足场景应失败而不是跳过后算通过。

72 小时测试在独立后台运行，只读状态文件查看结果，不在聊天中循环等待。休眠/唤醒、重试和故障证据应记录；时间经过不等于测试通过。当前没有新的完整 72 小时通过证据。

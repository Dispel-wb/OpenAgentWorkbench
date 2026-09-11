# Windows Sandbox 启用与验收方案

状态：仅准备方案。尚未启用 Windows 功能、重启电脑或执行干净系统验收。

## 1. 主机启用（由用户执行）

本机检查：Windows 11 Pro 25H2，约 31 GB 内存，虚拟化正在运行；未找到 Windows Sandbox 可执行文件，也没有 Hyper-V 管理模块或其他现成虚拟机工具。仍需由管理员核实可选功能状态。

先保存工作并安排重启时间。以管理员身份打开 PowerShell：

```powershell
Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All -NoRestart
```

第二条命令会启用系统功能，但不自动重启。若提示需要重启，请由用户手动重启；不要在仍需保留连续运行证据时重启。旧版 soak 可以保留记录并标注被新候选替代，新候选的 72 小时应在系统启用和候选冻结之后重新开始。

重启后从开始菜单打开 Windows Sandbox。Windows 11 24H2 及更新系统可能需要从 Microsoft Store 获取新版 Sandbox。不要关闭 Defender、SmartScreen、驱动签名或其他系统保护来运行测试。

## 2. 隔离测试配置

准备专门的候选输入文件夹及空的结果文件夹，不共享用户主目录、真实工作区、浏览器数据或密钥。配置应采用：

- 候选 EXE、测试脚本、锁定依赖：只读映射；在客体 C 盘复制后运行。
- 测试结果：仅映射专用空文件夹为可写；不把该文件夹视作可信输入自动执行。
- 网络、剪贴板、麦克风、摄像头、打印机重定向：默认禁用。
- 内存：8192 MB；先测兼容渲染，GPU 路径另做一轮。
- 如需 WebView2，使用预先下载且核验来源的固定运行时，或者单独批准联网安装官方 Evergreen Runtime；不静默打开网络。

暂不生成带有过期 EXE 路径的启动配置。候选冻结后，根据最终文件摘要生成对应的 .wsb 文件和输入清单。

## 3. 验收范围

在客体内执行安装、启动、升级/previous 备份、卸载保留工作区、无 D 盘、中文空格路径、离线 CLI 夹具、SQLite 完整性及 UI 恢复测试。记录候选 SHA-256、实际 OS build、账户权限和 WebView2 版本。

Sandbox 默认客体账户是管理员；其通过结果不能冒充普通用户或中文用户名验收。这两项需要客体内另建测试账户并实测。Windows 11 Sandbox 也不能代替 Windows 10 实机矩阵。

仅在所有断言实际通过后产生 passed 报告；缺少运行时、环境不匹配或失败均保留具体原因。关闭 Sandbox 会清除未映射的客体数据，先导出已脱敏结果。

## 官方依据

- [安装 Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install)
- [WSB 配置与文件夹映射](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
- [Sandbox 版本和 Store 更新](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-versions)

# 可复现构建

构建使用固定 NuGet 版本和下载 SHA-256、固定资源枚举顺序、Roslyn deterministic 标志和路径映射。发布 CI 固定 Python/Pillow 版本，DSHarness 可选运行时由独立 npm 锁文件管理。

```powershell
.\restore_build_dependencies.ps1
.\tests\reproducible-build.ps1 -Edition OpenSource -DependencyRoot .\.packages -PythonPath (Get-Command python).Source -ReportPath .\dist\reproducible-build.json
```

测试构建两份未签名 EXE 并逐字节摘要比较，输出 EXE 摘要、大小与依赖锁摘要。此测试验证同一环境内的重复构建，**不证明不同 OS、框架引用程序集或 Python 版本间必然一致**。跨机器复现仍需独立构建者提供证据。

验证顺序：检出相同 commit → 空缓存恢复依赖 → 使用匹配工具版本 → 构建 → 比较 SHA-256。依赖缓存 marker 不等于对缓存所有文件的完整性审计；发布环境应从干净 checkout/cache 恢复。

压缩包时间戳和代码签名会影响二进制摘要。当前门禁比较未签名 EXE，不宣称 ZIP 字节可复现。`SOURCE_DATE_EPOCH` 记录在公共源清单中，不替代编译器/图像生成工具版本锁定。

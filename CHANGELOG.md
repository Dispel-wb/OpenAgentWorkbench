# Changelog

## 0.7.0-preview.1 — unreleased candidate

- 增加真正的 Codex CLI exec/resume 和 DSHarness CLI SDK 核心，独立于 Claude Code 安装。
- 加入核心选择、Codex 自有模型配置、DSHarness 锁定运行时和能力预检。
- 修复中文 stdin、Codex 累计 Token、缺失结束事件、失败事件、恢复参数和无 Provider 启动路径。
- 非 Claude 核心暂不支持的交互审批运行前拒绝；优先队列按核心能力延后执行。
- 增加锁定构建依赖、可复现构建脚本、CI/CD 与干净 Windows 测试入口。
- 长期测试增加休眠/唤醒识别、有限重试、失败证据；不占用交互任务等待。
- 提取 Artifact Store、UI Store 和 Provider catalog，补充开发和安全文档。

候选仍未完成 72 小时稳定性、全部干净 Windows 矩阵和独立安全审计，未声明 1.0.0。

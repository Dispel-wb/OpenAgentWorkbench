# 数据与恢复

主数据位于工作区下 `.claude-gui-v2`。当前事件库 Schema V11；准确字段与迁移以 `AgentEventStore.cs` 为准，不把本文当作可直接运行的 SQL。

| 数据 | 作用 | 备份注意 |
| --- | --- | --- |
| agent-store.db + WAL/SHM | 任务、运行、事件游标、工具审计、制品、记忆、调度与 Provider 健康记录 | 运行中不能只复制 db 而忽略 WAL |
| settings.json | 界面/工作区与核心选择 | 可含本机路径，不用于公开夹具 |
| providers-v2.json | Provider 配置、DPAPI 保护的 token | 密文绑定当前 Windows 用户；不是跨机器便携密钥 |
| runs、messages | 运行输入、流式输出、错误和恢复证据 | 可能含提示词和工具输出，需脱敏 |
| worker-sessions/codex | 工作台会话到 Codex thread 的映射与累计用量 | Codex 自有会话数据也必须存在 |
| worker-homes/dsh | DSHarness 隔离 home | 跨进程恢复尚未验收 |

归档仅隐藏活动聊天列表，不等于删除。永久删除与数据库清理应由对应功能处理；不同 CLI 自有 transcript 的删除/保留方式不保证相同。不要手删数据文件以修复一次失败。

安全备份：等待任务结束或暂停，从托盘退出 Host，复制整个数据目录及工作区。恢复前保留当前目录；升级测试需验证重复迁移幂等。数据库不支持自动降级，回滚 EXE 不等于回滚 Schema，应使用配套升级前备份。

# 发布流程

1. 候选保持 Preview；更新 EditionInfo、Assembly metadata 和 CHANGELOG，版本必须一致。
2. 公共目录仅同步允许分发的源码、测试和文档，保留 Git 历史，排除用户数据、私有图像、诊断包与二进制缓存。
3. 在干净目录恢复依赖，构建 OpenSource EXE，执行 CI 离线回归、安全扫描、可复现构建。
4. 检查差异和许可证；确认没有 token、个人路径、外部产品角色图像。安全扫描是自动化门禁，不是完整人工安全审计。
5. 先推送候选 commit。CI 实际成功后才创建匹配的 `v<version>` 标签。Release workflow 构建 ZIP、SHA256SUMS 和门禁证据。
6. 未通过干净系统矩阵、长期稳定性和安全复核，不标为 1.0.0。自签证书不能证明公众信任；没有可信签名时明确 unsigned，不要求用户关闭系统保护。

GitHub Actions 的实际状态以仓库 Actions 为准。本地 YAML 解析成功不等于远程 CI 成功。手动 workflow_dispatch 只生成待审查构建产物；标签推送才发布 GitHub Release。

更新本地安装前确认没有活动任务，验证 EXE 摘要并保留可恢复版本。回退时同时核对数据 Schema，不用旧 EXE 强行写入新 Schema。安装与卸载不得自动删除工作区数据。

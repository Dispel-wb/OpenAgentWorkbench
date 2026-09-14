# 发布流程

1. 候选必须明确标为未发布；更新 EditionInfo、Assembly metadata 和 CHANGELOG，版本必须一致。长期测试可冻结最终版本元数据，以验证与待发布文件完全一致的字节；元数据不代表验收通过。
2. 公共目录仅同步允许分发的源码、测试和文档，保留 Git 历史，排除用户数据、私有图像、诊断包与二进制缓存。
3. 在干净目录恢复依赖，构建 OpenSource EXE，执行 CI 离线回归、安全扫描、可复现构建。
4. 检查差异和许可证；确认没有 token、个人路径、外部产品角色图像。安全扫描是自动化门禁，不是完整人工安全审计。
5. 先推送候选 commit。CI 实际成功后才创建匹配的 `v<version>` 标签。Release workflow 构建 ZIP、SHA256SUMS 和门禁证据。
6. 未通过干净系统矩阵、长期稳定性和安全复核，不发布正式版 1.0.0。自签证书不能证明公众信任；没有可信签名时明确 unsigned，不要求用户关闭系统保护。

GitHub Actions 的实际状态以仓库 Actions 为准。本地 YAML 解析成功不等于远程 CI 成功。手动 workflow_dispatch 只生成待审查构建产物；标签推送才发布 GitHub Release。

更新本地安装前确认没有活动任务，验证 EXE 摘要并保留可恢复版本。回退时同时核对数据 Schema，不用旧 EXE 强行写入新 Schema。安装与卸载不得自动删除工作区数据。

## 1.0 及后续正式版证据门禁

`tests/require-v1-readiness.ps1` 在 Release 构建之后、打包之前执行。正式版本号主版本至少为 1 时，必须有 `release-evidence/<version>/readiness.json` 及六份报告；缺失即停止。Preview 与 0.x 不要求该证据，但返回 `NOT_REQUIRED`，不能据此宣称获得正式版认证。构建元数据中的连字符不会绕过正式版检查。

清单字段：`schemaVersion: 1`、`version`、`candidateSha256`、`status: "passed"`、`reviewedBy`、`reports`。每条 reports 记录包含 `category`、`file`、`sha256`，文件名固定为 `<category>.json`，类别如下。所有摘要使用 SHA-256；候选摘要必须等于此次 Release 实际构建的 EXE，而不是旧候选。报告提交可改变 Git commit，但不得改变待认证 EXE；同提交 CI 门禁另行保留。

每份报告共有字段：`schemaVersion: 1`、`category`、`candidateSha256`、`status: "passed"`、`method`、`reviewedBy`、`findings`（非空数组）、`completedAt`（带时区的 ISO 8601）。各类别补充字段：

| category | 必需内容 |
| --- | --- |
| soak | `startedAt`、`activeDurationSeconds` 至少 259200 且不超过起止时间差、`failures: 0`、`restarts: 0`、`agentWorkload: "passed"`；单纯空闲探活不能满足 agent 工作负载要求 |
| windows | `cases` 每项有 name、status、environment；必须通过 clean-windows-10、clean-windows-11、standard-user、unicode-username、no-d-drive、install-upgrade-uninstall |
| native-performance | `renderer: "WebView2"`、`datasetKind: "real-conversation"`、`bodyCharacters` 至少 100000、`datasetSha256`、非空 `measurements`；记录实际耗时、内存、测试动作、阈值和结果 |
| accessibility | `keyboard: "passed"`、`screenReader: "passed"`；method/findings 说明使用的读屏器、版本和覆盖操作 |
| security-review | `independentReview: "passed"`、`unresolvedHighOrCritical: 0`；记录独立复核者及范围，不用自动扫描代替 |
| regression | `failed: 0`、`skipped: 0`、正数 `total`；findings 列明测试清单与结果 |

这些是人工审阅的证据摘要，不是密码学证明测试确实执行。哈希检查防止摘要与文件错配，不能辨别故意伪造的报告；维护者必须核验原始日志、环境与覆盖面后签署 reviewedBy。不要将测试夹具、私有对话正文、凭据或个人路径写入公共证据；公开脱敏结果，真实数据只公开摘要。当前没有上述正式版证据，门禁应保持拒绝。证据通过也不会自动授权合并、打标签或发布。

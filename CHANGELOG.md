# Changelog

## Unreleased — 1.0.0 readiness work

- Unified settings and peripheral control surfaces on deep blue for Open Agent, Codex and mixed skins in both light and dark modes, including top selectors, file-tree glyphs, API capability cards, text and matched inner/outer focus frames; only the Local Claude skin retains the warm-brown treatment.
- Moved per-message deletion exclusively to the right-click menu, removed the message-header red ×, and limited quoting to selected text through that same menu; regeneration and answer-version controls remain available.
- Fixed recursive workspace snapshots by excluding `.claude-gui-v2` and `.git` at every depth during snapshot, change detection and rollback; stale nested run snapshots can no longer recursively copy Workbench state into themselves.
- Expanded Pi 0.85.1 on Windows: read/plan now expose workspace-scoped read/search/list tools, edit/agent expose workspace-scoped file edits, scoped mode honors its file-tool allowlist, and Full uses Pi's actual `powershell` tool instead of the unavailable `bash` name.
- Added a fail-closed Pi policy extension that canonicalizes file paths, blocks access outside the selected workspace and protects `.git`/`.claude-gui-v2` writes. PowerShell remains Full-only because this policy is not an OS sandbox.
- Fixed build bootstrap so a stale, non-runnable `.venv` launcher is rejected instead of being selected merely because the file exists.
- Made atomic JSON persistence use collision-free temporary files with bounded contention retries, preventing concurrent Host/Worker status writers from turning an already durable success into a sharing-violation failure.
- Verified the current unsigned OpenSource bytes with a fresh 70/70 offline regression and same-environment reproducible build. This does not satisfy the new 72-hour or clean-Windows requirements.
- Candidate binaries now carry 1.0.0 metadata so the exact intended release bytes can undergo long-term testing. This is an unreleased candidate, not a completed certification or published stable release.
- Fixed duplicate rendering when a buffered streaming delta and terminal result arrive in the same poll; verified the old failure and corrected behavior in native WebView2.
- Fixed terminal polling returning stale metadata when background reconciliation wins the finalization race; a controlled two-case regression reproduces the old failure without relying on timing luck.
- Added an isolated 70-case offline regression runner with binary/script hashes and retained per-case logs; corrected stale Local-edition assumptions, explicit UTF-8 decoding, inherited provider defaults and missing offline CLI/fallback fixtures. All 100 fault cycles retain their assertions with a separate hosted-runner time budget.
- Added timed same-Host Pi workload validation with repeated two-turn conversations, permission rejection, process-tree cancellation, SQLite/health checks and active-time accounting that excludes long suspension gaps.
- Fixed cancelled and retired native Workers retaining process resources. A 60-cycle gate now verifies that Host handles plateau after warm-up; the old candidate continued to grow while the corrected candidate stayed within the bound.
- Preserved cancelled image jobs in the in-memory result index after resource cleanup so image poll/file requests keep their terminal contract.

- Added the optional, locked Pi 0.85.1 native RPC adapter with explicit permission limits, persisted sessions, process-tree cancellation and genuine loopback-model tests.
- Fixed fresh-start workspace precedence so the UI does not replace the Host's C-drive or Unicode workspace with a hard-coded D-drive default.
- Added a shared raw-frame retention budget for DSHarness, covering both the mailbox and pre-acknowledgement notifications.
- Extended CI to restore and test the actual Pi runtime and reject workspace/bootstrap and aggregate-buffer regressions.
- Signing is an explicitly accepted exemption for the requested 1.0.0; all other readiness claims still require evidence. No stable version is declared by this entry.

## 0.7.0-preview.1 — unreleased candidate

- 增加真正的 Codex CLI exec/resume 和 DSHarness CLI SDK 核心，独立于 Claude Code 安装。
- 加入核心选择、Codex 自有模型配置、DSHarness 锁定运行时和能力预检。
- 修复中文 stdin、Codex 累计 Token、缺失结束事件、失败事件、恢复参数和无 Provider 启动路径。
- 非 Claude 核心暂不支持的交互审批运行前拒绝；优先队列按核心能力延后执行。
- 增加锁定构建依赖、可复现构建脚本、CI/CD 与干净 Windows 测试入口。
- 长期测试增加休眠/唤醒识别、有限重试、失败证据；不占用交互任务等待。
- 提取 Artifact Store、UI Store 和 Provider catalog，补充开发和安全文档。

候选仍未完成 72 小时稳定性、全部干净 Windows 矩阵和独立安全审计，未声明 1.0.0。

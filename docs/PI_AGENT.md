# Pi Coding Agent adapter

The workbench supports the genuine `@earendil-works/pi-coding-agent` **0.85.1** through its native RPC mode. This is an optional runtime, not a reimplementation of Pi. Other versions fail validation because completion depends on the `agent_settled` contract.

## Setup

Install Node.js 22.19+ and run `./install_pi_runtime.ps1 -InstallRoot <workbench-directory>`. It restores the checked-in npm lockfile in `runtimes/pi`, with lifecycle scripts disabled. It does not install anything globally or overwrite the workbench EXE.

In **Settings → Workspace → Agent Worker SDK**, choose **Pi Coding Agent (RPC mode)**. An adjacent runtime is detected automatically. Alternatively choose the absolute `node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js` entry and Node executable. Select a text provider and model in the existing API settings.

## Effective behavior

| Setting | Pi behavior |
|---|---|
| Readonly / plan | Workspace-scoped read/grep/find/ls |
| Scoped | Selected file tools, restricted to the workspace; shell is not exposed |
| Edit / agent | Workspace-scoped read/edit/write/grep/find/ls; shell is not exposed |
| Full | Pi read/PowerShell/edit/write/grep/find/ls on Windows; **no workspace sandbox or per-call approval** |
| Manual | Rejected before execution; interactive approval is not yet represented |
| Disallowed tools | Removed from the Pi startup allowlist |
| Attachments | Rejected; not silently dropped |
| OpenAI text provider | Native Chat Completions, Bearer authentication |
| Anthropic text provider | Native Messages, x-api-key authentication |
| Non-native explicit auth style | Rejected before launching Pi |
| Thinking effort | Not applied in this first adapter; ordinary text mode (`off`) |
| Steering while busy | Next-turn queue, not live steering |
| Stop | Terminate the bridge and its Pi process tree |
| Next message / process restart | Resume the native session file |
| Unexpected process loss | Fail closed; do not replay potentially side-effecting input |

Pi starts without automatic project/user extensions, context-file discovery, skills, prompt templates or themes. A bundled Workbench policy extension is loaded explicitly for every non-Full run. It canonicalizes file targets, blocks paths outside the selected workspace, and protects `.git` and `.claude-gui-v2` writes. This is a fail-closed application policy, not an OS security boundary; PowerShell is therefore exposed only in Full mode. Workbench-approved instruction text is passed explicitly. Workbench MCP servers and agent definitions are not represented as enforced Pi policy. Pi startup network work and telemetry are disabled. API credentials are passed through the process environment, not command arguments or models.json; this is not OS-level isolation from other same-user processes.

State pointers live in the workspace's `.claude-gui-v2/worker-sessions/pi/`, with native sessions in adjacent per-conversation directories. Each workbench turn starts a fresh Pi process and resumes its session. Missing session files produce a visible error rather than silently dropping history. Retry and automatic compaction are disabled for this first adapter; errors remain visible. Each turn uses the workbench maximum-turn guard.

## Verification

`node tests/pi-worker-sdk-integration.js <candidate.exe>` covers 18 cases: fake transport faults, invalid permissions, Chinese streaming and usage, delayed settlement, actual Pi cross-process resume, native file writing, real API failure and both OpenAI/Anthropic transports. All model endpoints are loopback fixtures; no paid model requests.

`./tests/pi-host-integration.ps1 -Executable <candidate.exe>` covers two host turns, seven preflight rejection cases, persisted native history and process-tree cancellation. `tests/pi-ui-visual.js` checks the persisted selector, runtime diagnostics and 1280/826px layouts.

This candidate adds a new runtime and is **not covered by the earlier frozen 72-hour soak**. It does not change the frozen artifact or imply a completed v1 release/CI run.

Upstream: https://github.com/earendil-works/pi/tree/main/packages/coding-agent

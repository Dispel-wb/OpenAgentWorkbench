# Security Policy

## Supported versions

Public Preview releases receive best-effort security fixes. Version 1.0.0 has not been declared stable yet.

## Reporting a vulnerability

Please use the repository's private GitHub Security Advisory workflow. Do not include real API keys, access tokens, private documents, diagnostic archives or database files in a public issue.

Include the affected version, Windows version, reproduction steps and a redacted diagnostic summary. If a token may have been exposed, revoke it before reporting.

## Local trust boundary

The application runs with the current Windows user's privileges. Claude's workbench permission broker, Codex's sandbox and DSHarness's native policy are distinct boundaries. The Codex/DSHarness adapters reject unsupported workbench interactive approvals and tool filters instead of silently ignoring them. Full-access mode deliberately broadens access. CLI-owned hooks, MCP servers, Skills, imported skins and provider endpoints must be treated as untrusted input.

Automated regression and release scanning do not constitute a complete independent security audit. See [the threat model](docs/THREAT_MODEL.md) and [CLI capability boundaries](docs/AGENT_WORKER_SDK.md). Current builds remain unsigned while the project applies for the free SignPath open source program. Signed candidates must follow the [code signing policy](CODE_SIGNING_POLICY.md) and repeat every release gate against the signed SHA-256.

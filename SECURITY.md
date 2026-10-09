# Security Policy

## Supported versions

Public Preview releases receive best-effort security fixes. Version 1.0.0 has not been declared stable yet.

## Reporting a vulnerability

Please use the repository's private GitHub Security Advisory workflow. Do not include real API keys, access tokens, private documents, diagnostic archives or database files in a public issue.

Include the affected version, Windows version, reproduction steps and a redacted diagnostic summary. If a token may have been exposed, revoke it before reporting.

## Local trust boundary

The application runs with the current Windows user's privileges. Claude's workbench permission broker, Codex's sandbox and DSHarness's native policy are distinct boundaries. The Codex/DSHarness adapters reject unsupported workbench interactive approvals and tool filters instead of silently ignoring them. Full-access mode deliberately broadens access. CLI-owned hooks, MCP servers, Skills, imported skins and provider endpoints must be treated as untrusted input.

Automated regression and release scanning do not constitute a complete independent security audit. See [the threat model](docs/THREAT_MODEL.md) and [CLI capability boundaries](docs/AGENT_WORKER_SDK.md). SignPath Foundation declined the free certificate application because the project does not yet have enough external reputation signals. Portable builds use GitHub and Sigstore provenance without claiming Authenticode trust; Microsoft Store packages receive a Windows-trusted signature only after Store certification. Every promoted artifact must repeat the applicable release gates against its final SHA-256.

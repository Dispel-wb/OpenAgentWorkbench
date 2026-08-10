# Security Policy

## Supported versions

Public Preview releases receive best-effort security fixes. Version 1.0.0 has not been declared stable yet.

## Reporting a vulnerability

Please use the repository's private GitHub Security Advisory workflow. Do not include real API keys, access tokens, private documents, diagnostic archives or database files in a public issue.

Include the affected version, Windows version, reproduction steps and a redacted diagnostic summary. If a token may have been exposed, revoke it before reporting.

## Local trust boundary

The application runs commands and file operations with the permissions of the current Windows user. Workspace-external access and dangerous operations require explicit approval unless the user deliberately enables a broader permission mode. Imported skins, MCP servers, Skills and provider endpoints must be treated as untrusted input.

# Privacy policy

Open Agent Workbench does not include first party telemetry, advertising, or analytics, and does not send conversation content to the project maintainers.

## Data stored locally

The application stores conversations, task state, workspaces, configuration, databases, logs, diagnostics, extension metadata, and update settings on the user's computer. Provider secrets are protected with Windows DPAPI for the current user. The local service listens on `127.0.0.1` and uses an installation specific authentication key.

## Network access

Network requests occur only for features the user configures or invokes, including:

- requests to a selected AI provider or compatible endpoint;
- CLI, MCP server, Skill, plugin, or extension operations selected by the user;
- catalog description refreshes and downloads;
- checks or staged downloads from a configured update manifest;
- links the user chooses to open.

Those services process data under their own privacy policies. Users choose the endpoint, credentials, files, and content supplied to them. The application should not be given confidential data unless the selected service and its terms are appropriate for that data.

## Diagnostics and deletion

Diagnostic exports are created locally. Users should inspect them before sharing because paths, machine names, task summaries, or provider responses may remain relevant to diagnosis.

Users can delete chats and task files from the application, remove configured credentials, and uninstall the application. Runtime data under the configured workspace and application data directories may be removed separately after the application and background host are stopped. Backups and files copied to external services must be deleted through those services.

Security issues should be reported privately as described in [SECURITY.md](SECURITY.md).

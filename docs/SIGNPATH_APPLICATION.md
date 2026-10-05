# SignPath open source application

This document records the public project information and the remaining account steps for the free SignPath open source program.

## Project details

- Project name: Open Agent Workbench
- Repository and homepage: https://github.com/Dispel-wb/OpenAgentWorkbench
- Releases: https://github.com/Dispel-wb/OpenAgentWorkbench/releases
- License: OSI approved MIT License
- Platform: Windows 10/11 x64 desktop application
- Description: a local Windows desktop workbench for Agent conversations, task queues, permission mediation, terminals, Skills, MCP integrations, and multiple CLI/model providers.
- Maintainer, author, reviewer, and signing approver: https://github.com/Dispel-wb
- Privacy policy: https://github.com/Dispel-wb/OpenAgentWorkbench/blob/main/PRIVACY.md
- Code signing policy: https://github.com/Dispel-wb/OpenAgentWorkbench/blob/main/CODE_SIGNING_POLICY.md
- Trusted build workflow: `.github/workflows/signpath-candidate.yml`

The repository contains independently implemented project code under MIT. Third party notices and the front end provenance statement are published separately. The application is not malware, an exploitation tool, or a mechanism for bypassing operating system security.

## SignPath project configuration

Create one artifact configuration for a GitHub Actions artifact ZIP:

```xml
<zip-file>
  <pe-file path="OpenAgentWorkbench.exe" />
</zip-file>
```

Only `OpenAgentWorkbench.exe` is a signing target. The signing policy must require manual approval. The trusted build system is the public GitHub repository and the `signpath-candidate.yml` workflow on GitHub hosted `windows-2022` runners.

Configure these GitHub repository values after SignPath provisions the project:

- Secret: `SIGNPATH_API_TOKEN`
- Variable: `SIGNPATH_ORGANIZATION_ID`
- Variable: `SIGNPATH_PROJECT_SLUG`
- Variable: `SIGNPATH_SIGNING_POLICY_SLUG`
- Variable: `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`

The workflow produces a signed candidate and a promotion manifest. It has no release publishing permission.

## Account actions still required

The maintainer must supply an email address directly to SignPath, confirm that GitHub and SignPath multifactor authentication are enabled, accept the current SignPath terms, and submit the application. These identity statements must not be inferred or filled by automation. After approval, the maintainer adds the provisioned secret and variables and manually runs the signing workflow.

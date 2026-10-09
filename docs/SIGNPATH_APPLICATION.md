# SignPath open source application

This document records the public project information and the result of the free SignPath open source application.

## Application result

SignPath Foundation declined the application in October 2026. The stated reason was that the project did not yet have enough external trust and visibility signals, such as community adoption, independent articles or discussions, institutional backing, and sustained public engagement. No Foundation certificate has been issued, and current artifacts must not be described as SignPath signed.

The project may reapply after those public signals develop. A regular paid subscription is also available, but it is not part of the current release plan. The immediate free paths are Microsoft Store signing for the certified MSIX and GitHub/Sigstore provenance for the standalone EXE.

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

## Steps required before a future reapplication

Build public adoption, independent references, contributor activity, and sustained release history. If SignPath later approves a new application, the maintainer must configure the provisioned secret and variables, review the current terms, and manually run the candidate workflow. Identity and private contact details are never stored in this repository.

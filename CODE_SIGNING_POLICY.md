# Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Scope

Only the Windows x64 `OpenAgentWorkbench.exe` built from this public repository may be signed. A signing request must originate from the GitHub hosted workflow in `.github/workflows/signpath-candidate.yml`, refer to an immutable commit, and use the uploaded GitHub Actions artifact produced by that same run. The artifact configuration is a ZIP whose only signable product is `OpenAgentWorkbench.exe`.

Signing never covers user data, configuration, logs, downloaded extensions, bundled upstream binaries, or locally rebuilt executables. A valid signature proves the publisher and artifact integrity; it does not replace security review or runtime permission controls.

## Roles and approval

The current project team is:

- Author and maintainer: [Dispel-wb](https://github.com/Dispel-wb)
- Reviewer: [Dispel-wb](https://github.com/Dispel-wb)
- Signing approver: [Dispel-wb](https://github.com/Dispel-wb)

Repository and SignPath accounts used for signing must have multifactor authentication enabled. Each release signing request requires manual approval after the commit, test evidence, unsigned SHA-256, and requested version have been reviewed. The API token is stored only as a GitHub Actions secret. Project identifiers are stored as repository variables.

## Release procedure

1. Build on a GitHub hosted Windows runner from locked dependencies.
2. Verify reproducibility, run the full offline regression suite, and run the release security audit.
3. Upload the unsigned executable as an immutable workflow artifact.
4. Submit that artifact to SignPath and manually approve the request.
5. Verify Authenticode, product metadata, the signed SHA-256, and the exact signing request.
6. Repeat clean Windows, accessibility, independent security, and 72 hour four core validation against the signed SHA-256 before promotion.
7. Publish the already verified signed artifact without rebuilding it.

The signing workflow creates a candidate artifact only. It cannot create or modify a GitHub Release.

## Incident response

If a signing credential, account, workflow, or released binary may be compromised, maintainers stop signing and distribution, preserve the affected hashes and logs, notify SignPath, request certificate revocation when appropriate, and publish a security advisory. A replacement build receives a new version and repeats every release gate.

See the [privacy policy](PRIVACY.md), [security policy](SECURITY.md), and [release process](docs/RELEASE_PROCESS.md).

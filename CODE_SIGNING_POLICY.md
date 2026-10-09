# Code signing and provenance policy

Open Agent Workbench uses separate trust mechanisms for its two Windows distribution formats.

## Portable EXE

The portable `OpenAgentWorkbench.exe` is built on a GitHub-hosted Windows runner from a public immutable commit. The provenance workflow runs the full offline regression and release security audit before it creates:

- a GitHub artifact attestation;
- a Sigstore keyless bundle tied to the repository, workflow path, and Git ref;
- a SHA-256 checksum file.

These records prove artifact origin and integrity. They are not Authenticode signatures and do not establish a Windows publisher identity. Windows may still show an unknown-publisher warning.

## Microsoft Store MSIX

The Store package uses the exact identity values assigned by Partner Center. The local build is unsigned and is uploaded only for Store certification. Microsoft signs the accepted MSIX and manages its installation, update, and uninstall. The Store signature covers the certified package; it does not sign a separately downloaded portable EXE.

## SignPath status

The project applied to the free SignPath Foundation program in October 2026. SignPath declined the application because the project did not yet show enough public adoption, independent references, sustained engagement, or institutional backing. The existing SignPath candidate workflow remains dormant for a future reapplication and must not be described as an active signing service.

## Release controls

1. Build from locked dependencies on a GitHub-hosted Windows runner.
2. Verify reproducibility, run the full offline regression, and run the release security audit.
3. Create provenance for the exact portable EXE or submit the exact MSIX to Partner Center.
4. Record the final SHA-256 and verification result.
5. Repeat clean Windows, accessibility, independent security, and 72-hour four-core validation against the final promoted SHA-256.
6. Publish the already verified artifact without rebuilding it.

The provenance workflow cannot create or modify a GitHub Release. Store submission and release promotion require explicit maintainer action.

If a workflow identity, repository, account, or released binary may be compromised, maintainers stop distribution, preserve the affected hashes and logs, revoke or remove affected artifacts when possible, and publish a security advisory.

See the [provenance guide](docs/PROVENANCE.md), [Microsoft Store guide](docs/MICROSOFT_STORE.md), [privacy policy](PRIVACY.md), [security policy](SECURITY.md), and [release process](docs/RELEASE_PROCESS.md).

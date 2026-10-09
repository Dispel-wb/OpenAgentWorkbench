# Build provenance verification

The portable EXE is accompanied by two independent provenance mechanisms:

- GitHub artifact attestation binds the SHA-256 to this repository and a GitHub Actions workflow run.
- A Sigstore keyless bundle binds the file to `.github/workflows/provenance.yml` through GitHub's OIDC identity and the public transparency log.

These mechanisms prove build origin and integrity. They are not Windows Authenticode signatures and do not remove the “unknown publisher” warning for a standalone EXE.

## Verify the GitHub attestation

After downloading the candidate from the workflow artifact:

```powershell
gh attestation verify .\OpenAgentWorkbench.exe --repo Dispel-wb/OpenAgentWorkbench
```

## Verify the Sigstore bundle

Install Cosign from the official Sigstore release, then run:

```powershell
cosign verify-blob .\OpenAgentWorkbench.exe `
  --bundle .\OpenAgentWorkbench.exe.sigstore.json `
  --certificate-identity 'https://github.com/Dispel-wb/OpenAgentWorkbench/.github/workflows/provenance.yml@refs/heads/main' `
  --certificate-oidc-issuer 'https://token.actions.githubusercontent.com'
```

Use the exact ref shown by the workflow run when the candidate comes from a tag or another branch.

The provenance workflow cannot publish a GitHub Release. Promotion still requires the release checklist and validation against the final SHA-256.

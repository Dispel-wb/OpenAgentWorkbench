$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$policy = Get-Content -LiteralPath (Join-Path $root 'CODE_SIGNING_POLICY.md') -Raw
$privacy = Get-Content -LiteralPath (Join-Path $root 'PRIVACY.md') -Raw
$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\signpath-candidate.yml') -Raw
$provenance = Get-Content -LiteralPath (Join-Path $root '.github\workflows\provenance.yml') -Raw

foreach ($required in @(
    'GitHub artifact attestation', 'Sigstore keyless bundle', 'not Authenticode signatures',
    'Microsoft Store MSIX', 'SignPath declined the application', 'final SHA-256'
)) {
    if (-not $policy.Contains($required)) { throw "Signing policy is missing: $required" }
}
if (-not $readme.Contains('## Code signing policy') -or -not $readme.Contains('CODE_SIGNING_POLICY.md') -or -not $readme.Contains('PRIVACY.md')) { throw 'README signing/privacy links are incomplete' }
if (-not $privacy.Contains('does not include first party telemetry') -or -not $privacy.Contains('Windows DPAPI') -or -not $privacy.Contains('127.0.0.1')) { throw 'Privacy boundary is incomplete' }
foreach ($required in @(
    'workflow_dispatch:', 'permissions:', 'actions: read', 'contents: read',
    'actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02',
    'signpath/github-action-submit-signing-request@f6d04783b4569d051e0c80105fe66e82819d0092',
    'wait-for-completion: true', 'Get-AuthenticodeSignature', 'signtool', 'release-security-audit.ps1'
)) {
    if (-not $workflow.Contains($required)) { throw "Signing workflow is missing: $required" }
}
if ($workflow -match '(?im)^\s*(gh\s+release|softprops/action-gh-release|actions/create-release)') { throw 'Signing workflow must not publish a release' }
foreach ($required in @('id-token: write','attestations: write','actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8','cosign sign-blob','cosign verify-blob')) {
    if (-not $provenance.Contains($required)) { throw "Provenance workflow is missing: $required" }
}
if ($provenance -match '(?im)^\s*(gh\s+release|softprops/action-gh-release|actions/create-release)') { throw 'Provenance workflow must not publish a release' }
Write-Output 'Signing, provenance, and dormant SignPath workflow contracts passed'

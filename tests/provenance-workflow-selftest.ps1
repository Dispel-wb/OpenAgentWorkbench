$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\provenance.yml') -Raw
$required = @(
    'id-token: write',
    'attestations: write',
    'contents: read',
    'actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8',
    'sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6',
    'cosign sign-blob',
    'cosign verify-blob',
    'OpenAgentWorkbench.exe.sigstore.json'
)
foreach ($item in $required) { if (-not $workflow.Contains($item)) { throw "Provenance workflow is missing: $item" } }
if ($workflow -match '(?m)^\s*(release|packages):\s*write\s*$' -or $workflow -match 'gh\s+release|softprops/action-gh-release') { throw 'Provenance workflow must not publish a release.' }
Write-Output 'provenance-workflow-selftest: ok'

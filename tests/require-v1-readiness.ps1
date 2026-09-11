param(
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') { throw 'Invalid release version' }
if ($Matches[1] -eq '0' -or $Matches.ContainsKey(4)) {
    [pscustomobject]@{ Readiness = 'NOT_REQUIRED'; Version = $Version; Reason = 'Pre-1.0 or prerelease; not stable certification' } | ConvertTo-Json -Compress
    return
}
$hash = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
$root = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$manifest = Get-Content -LiteralPath (Join-Path $root 'readiness.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $Version -or $manifest.candidateSha256 -ine $hash) { throw 'Readiness manifest does not match the release artifact' }
if ([string]::IsNullOrWhiteSpace($manifest.reviewedBy) -or $manifest.status -cne 'passed') { throw 'Readiness has not been reviewed and passed' }
$required = @('soak','windows','native-performance','accessibility','security-review','regression')
if (@($manifest.reports).Count -ne $required.Count) { throw 'Readiness requires exactly six evidence reports' }
foreach ($name in $required) {
    $entries = @($manifest.reports | Where-Object { $_.category -ceq $name })
    if ($entries.Count -ne 1) { throw "Missing or duplicate evidence: $name" }
    $entry = $entries[0]
    # Fixed basenames prevent reports escaping this version's evidence directory.
    if ($entry.file -cne "$name.json" -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid evidence reference: $name" }
    $path = Join-Path $root $entry.file
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked evidence is not allowed: $name" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.sha256) { throw "Evidence hash mismatch: $name" }
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 1 -or $report.category -cne $name -or $report.status -cne 'passed' -or $report.candidateSha256 -ine $hash) { throw "Evidence is incomplete or for another artifact: $name" }
    if ([string]::IsNullOrWhiteSpace($report.method) -or [string]::IsNullOrWhiteSpace($report.reviewedBy) -or @($report.findings).Count -eq 0) { throw "Evidence lacks method, reviewer or findings: $name" }
    $completed = [DateTimeOffset]::Parse($report.completedAt)
    if ($completed -gt [DateTimeOffset]::UtcNow) { throw "Evidence completion is in the future: $name" }
    switch ($name) {
        soak {
            $started = [DateTimeOffset]::Parse($report.startedAt)
            if (($completed - $started).TotalHours -lt 72 -or $report.activeDurationSeconds -isnot [ValueType] -or $report.activeDurationSeconds -lt 259200 -or $report.activeDurationSeconds -gt ($completed - $started).TotalSeconds -or $report.failures -ne 0 -or $report.restarts -ne 0 -or $report.agentWorkload -cne 'passed') { throw 'Soak must cover 72 active hours with agent workload and no failures/restarts' }
        }
        windows {
            foreach ($case in @('clean-windows-10','clean-windows-11','standard-user','unicode-username','no-d-drive','install-upgrade-uninstall')) {
                $results = @($report.cases | Where-Object { $_.name -ceq $case })
                if ($results.Count -ne 1 -or $results[0].status -cne 'passed' -or [string]::IsNullOrWhiteSpace($results[0].environment)) { throw "Missing Windows coverage: $case" }
            }
        }
        native-performance {
            if ($report.renderer -cne 'WebView2' -or $report.datasetKind -cne 'real-conversation' -or $report.bodyCharacters -isnot [ValueType] -or $report.bodyCharacters -lt 100000 -or $report.datasetSha256 -notmatch '^[0-9a-fA-F]{64}$' -or @($report.measurements).Count -eq 0) { throw 'Native performance requires a real 100k-character conversation and measurements' }
        }
        accessibility {
            if ($report.keyboard -cne 'passed' -or $report.screenReader -cne 'passed') { throw 'Keyboard and screen-reader validation are required' }
        }
        security-review {
            if ($report.independentReview -cne 'passed' -or $report.unresolvedHighOrCritical -ne 0) { throw 'Independent security review is required' }
        }
        regression {
            if ($report.failed -ne 0 -or $report.skipped -ne 0 -or $report.total -isnot [ValueType] -or $report.total -le 0) { throw 'Full regression must pass without skipped tests' }
        }
    }
}
[pscustomobject]@{ Readiness = 'PASS'; Version = $Version; CandidateSha256 = $hash; Reports = $required.Count } | ConvertTo-Json -Compress

$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'require-v1-readiness.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('oaw-readiness-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Write-Json($path, $value) { [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false)) }
try {
    $exe = Join-Path $root 'fixture.bin'
    [IO.File]::WriteAllText($exe, 'Policy fixture only; this is not an executable or real validation evidence.')
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $cases = @('pass','preview','pre-v1','stable-build-metadata','invalid-version','missing-directory','missing-report','wrong-artifact','tampered-report','pending','duplicate','future','short-soak','idle-soak','failed-soak','incomplete-windows','synthetic-data','short-data','chrome','no-screenreader','no-security-review','skipped-regression','missing-property','unsafe-path')
    foreach ($case in $cases) {
        $version = '1.0.0'
        if ($case -eq 'preview') { $version = '1.0.0-rc.1' }
        if ($case -eq 'pre-v1') { $version = '0.7.0' }
        if ($case -eq 'stable-build-metadata') { $version = '1.0.0+build-test' }
        if ($case -eq 'invalid-version') { $version = '1.0' }
        $dir = Join-Path $root $case
        New-Item -ItemType Directory -Path $dir | Out-Null
        $manifest = @{ schemaVersion=1; version=$version; candidateSha256=$hash; reviewedBy='POLICY FIXTURE'; status='passed'; reports=@() }
        foreach ($name in @('soak','windows','native-performance','accessibility','security-review','regression')) {
            $report = @{ schemaVersion=1; category=$name; candidateSha256=$hash; status='passed'; method='Synthetic policy fixture, never release evidence'; reviewedBy='POLICY FIXTURE'; findings=@('Policy fixture'); completedAt=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o') }
            switch ($name) {
                soak { $report.startedAt=[DateTimeOffset]::UtcNow.AddHours(-73).ToString('o'); $report.activeDurationSeconds=259200; $report.failures=0; $report.restarts=0; $report.agentWorkload='passed' }
                windows { $report.cases=@(@('clean-windows-10','clean-windows-11','standard-user','unicode-username','no-d-drive','install-upgrade-uninstall') | ForEach-Object { @{ name=$_; status='passed'; environment='Policy fixture only' } }) }
                native-performance { $report.renderer='WebView2'; $report.datasetKind='real-conversation'; $report.bodyCharacters=100000; $report.datasetSha256=$hash; $report.measurements=@(@{ metric='fixture'; value=1 }) }
                accessibility { $report.keyboard='passed'; $report.screenReader='passed' }
                security-review { $report.independentReview='passed'; $report.unresolvedHighOrCritical=0 }
                regression { $report.failed=0; $report.skipped=0; $report.total=1 }
            }
            if ($name -eq 'soak') {
                switch ($case) {
                    pending { $report.status='running' }
                    future { $report.completedAt=[DateTimeOffset]::UtcNow.AddHours(1).ToString('o') }
                    short-soak { $report.activeDurationSeconds=60 }
                    idle-soak { $report.agentWorkload='not-run' }
                    failed-soak { $report.failures=1 }
                    missing-property { $report.Remove('startedAt') }
                }
            }
            if ($name -eq 'windows' -and $case -eq 'incomplete-windows') { $report.cases=$report.cases[1..5] }
            if ($name -eq 'native-performance') {
                switch ($case) {
                    synthetic-data { $report.datasetKind='generated' }
                    short-data { $report.bodyCharacters=99999 }
                    chrome { $report.renderer='Chrome' }
                }
            }
            if ($name -eq 'accessibility' -and $case -eq 'no-screenreader') { $report.screenReader='not-run' }
            if ($name -eq 'security-review' -and $case -eq 'no-security-review') { $report.independentReview='not-run' }
            if ($name -eq 'regression' -and $case -eq 'skipped-regression') { $report.skipped=1 }
            $path = Join-Path $dir "$name.json"
            Write-Json $path $report
            $manifest.reports += @{ category=$name; file="$name.json"; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        }
        switch ($case) {
            wrong-artifact { $manifest.candidateSha256='0' * 64 }
            tampered-report { [IO.File]::AppendAllText((Join-Path $dir 'soak.json'), ' ') }
            duplicate { $manifest.reports[0]=$manifest.reports[1] }
            unsafe-path { $manifest.reports[0].file='../soak.json' }
            missing-report { Remove-Item -LiteralPath (Join-Path $dir 'soak.json') }
        }
        Write-Json (Join-Path $dir 'readiness.json') $manifest
        if ($case -in @('missing-directory','stable-build-metadata','preview','pre-v1')) { $dir=Join-Path $root 'does-not-exist' }
        $rejected=$false
        try { & $gate -Version $version -Executable $exe -EvidenceDirectory $dir | Out-Null } catch { $rejected=$true }
        if ($rejected -ne ($case -notin @('pass','preview','pre-v1'))) { throw "Readiness policy fixture failed: $case" }
    }
    [pscustomobject]@{ ReadinessPolicy='PASS'; Cases=$cases.Count; Note='Synthetic policy tests only; no candidate certification' } | ConvertTo-Json -Compress
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('oaw-readiness-policy-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

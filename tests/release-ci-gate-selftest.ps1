$ErrorActionPreference='Stop'
$gate=Join-Path $PSScriptRoot 'require-release-ci.ps1'
$commit='1111111111111111111111111111111111111111'
function gh { $global:LASTEXITCODE=0; ConvertTo-Json -InputObject @(@{check_runs=$fixtureChecks}) -Depth 8 -Compress }
$cases=@('success','failed','missing','pending','wrong-sha','wrong-app','latest-failed','latest-success')
foreach($case in $cases) {
    $fixtureChecks=@();$id=0
    foreach($name in @('build-test-audit','dependency-review','analyze')){$fixtureChecks+=@{id=++$id;name=$name;head_sha=$commit;status='completed';conclusion='success';app=@{slug='github-actions'}}}
    switch($case) {
        failed {$fixtureChecks[1].conclusion='failure'}
        missing {$fixtureChecks=@($fixtureChecks[0],$fixtureChecks[2])}
        pending {$fixtureChecks[1].status='in_progress'}
        wrong-sha {$fixtureChecks[1].head_sha='2222222222222222222222222222222222222222'}
        wrong-app {$fixtureChecks[1].app.slug='untrusted-fixture'}
        latest-failed {$fixtureChecks+=@{id=10;name='dependency-review';head_sha=$commit;status='completed';conclusion='failure';app=@{slug='github-actions'}}}
        latest-success {$fixtureChecks+=@{id=0;name='dependency-review';head_sha=$commit;status='completed';conclusion='failure';app=@{slug='github-actions'}}}
    }
    $rejected=$false
    try { & $gate -Repository fixture/repository -Commit $commit | Out-Null } catch {$rejected=$true}
    if($rejected -ne ($case -notin @('success','latest-success'))){throw "CI gate fixture failed: $case"}
}
[pscustomobject]@{ReleaseCiPolicy='PASS';Cases=$cases.Count}|ConvertTo-Json -Compress

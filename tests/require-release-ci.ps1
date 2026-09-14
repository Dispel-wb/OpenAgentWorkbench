param(
    [Parameter(Mandatory=$true)][string]$Repository,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$Commit
)
$ErrorActionPreference='Stop'
if($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'){throw 'Invalid repository name'}
$raw=& gh api "repos/$Repository/commits/$Commit/check-runs?per_page=100" --paginate --slurp
if($LASTEXITCODE -ne 0){throw 'Cannot verify remote checks; refusing release'}
$pages=ConvertFrom-Json -InputObject ($raw -join "`n")
$checks=@($pages|ForEach-Object{$_.check_runs})
$failures=@()
foreach($name in @('build-test-audit','dependency-review','analyze')) {
    $latest=$checks|Where-Object{$_.name -eq $name -and $_.head_sha -eq $Commit -and $_.app.slug -eq 'github-actions'}|Sort-Object id -Descending|Select-Object -First 1
    if($null -eq $latest){$failures+="Missing required check: $name"}
    elseif($latest.status -ne 'completed' -or $latest.conclusion -ne 'success'){$failures+="$name is $($latest.status)/$($latest.conclusion)"}
}
if($failures.Count){throw ('Release CI gate rejected commit: '+($failures -join '; '))}
[pscustomobject]@{ReleaseCi='PASS';Commit=$Commit;RequiredChecks=3}|ConvertTo-Json -Compress

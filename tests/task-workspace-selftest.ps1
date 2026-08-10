param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-workspace-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$process = Start-Process -FilePath $Executable -ArgumentList @('--workspace-selftest', $root) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    $errorFile = Join-Path $root 'workspace-selftest-error.txt'
    if (Test-Path -LiteralPath $errorFile) { Write-Output (Get-Content -LiteralPath $errorFile -Raw -Encoding UTF8) }
    throw "Task workspace self-test failed with exit code $($process.ExitCode)"
}
[pscustomobject]@{
    GitWorktreeIsolation = 'OK'
    GitDiffReview = 'OK'
    AtomicApply = 'OK'
    WorktreeRevert = 'OK'
    NonGitSnapshot = 'OK'
    NonGitRevert = 'OK'
    Root = $root
} | Format-List

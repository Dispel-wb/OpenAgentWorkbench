param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-security-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$process = Start-Process -FilePath $Executable -ArgumentList @('--security-selftest', $root) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Task security self-test failed with exit code $($process.ExitCode)" }
[pscustomobject]@{
    ReadInsideRoot = 'OK'
    WriteDeniedInReadonly = 'OK'
    OutsideRootDenied = 'OK'
    AgentExecute = 'OK'
    DestructiveCommandApproval = 'OK'
    ManualOutsideApproval = 'OK'
    ScopedToolDeny = 'OK'
} | Format-List

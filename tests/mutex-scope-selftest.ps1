param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$process = Start-Process -FilePath $executablePath -ArgumentList '--mutex-scope-selftest' -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Mutex scope self-test failed: $($process.ExitCode)" }
[pscustomobject]@{
    MutexScopeGate = 'PASS'
    RequiresTestMode = $true
    RequiresTempWorkspace = $true
    InstalledEditionCannotOverride = $true
} | Format-List

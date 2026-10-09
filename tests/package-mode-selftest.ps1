param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$process = Start-Process -FilePath $exe -ArgumentList '--package-mode-selftest' -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) { throw "Package identity self-test failed with exit code $($process.ExitCode)" }
Write-Output 'package-mode-selftest: ok'

param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-updater-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$staged = Join-Path $root 'staged.exe'
$target = Join-Path $root 'Workbench.exe'
$oldBytes = [Text.Encoding]::UTF8.GetBytes('old-version-fixture')
[IO.File]::WriteAllBytes($target, $oldBytes)
Copy-Item -LiteralPath $Executable -Destination $staged
$hash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
$invalid = Start-Process -FilePath $Executable -ArgumentList @('--apply-update', $staged, $target, ('0' * 64), '--no-launch') -Wait -PassThru
if ($invalid.ExitCode -eq 0) { throw 'Updater accepted an invalid hash' }
if ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($target)) -ne 'old-version-fixture') { throw 'Invalid update modified target' }
$valid = Start-Process -FilePath $Executable -ArgumentList @('--apply-update', $staged, $target, $hash, '--no-launch') -Wait -PassThru
if ($valid.ExitCode -ne 0) { throw "Updater failed: $($valid.ExitCode)" }
$previous = Join-Path $root 'Workbench.previous.exe'
if (-not (Test-Path -LiteralPath $previous)) { throw 'Previous version was not preserved' }
if ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($previous)) -ne 'old-version-fixture') { throw 'Previous version content mismatch' }
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'Installed target hash mismatch' }
[pscustomobject]@{ InvalidHashRejected = $true; AtomicReplace = 'OK'; PreviousPreserved = 'OK'; InstalledHash = $hash; Root = $root } | Format-List

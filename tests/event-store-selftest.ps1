param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-event-store-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$process = Start-Process -FilePath $Executable -ArgumentList @('--event-store-selftest', $root) -Wait -PassThru
if ($process.ExitCode -ne 0) {
    $detail = Join-Path $root '.claude-gui-v2\event-store-selftest-error.txt'
    if (Test-Path -LiteralPath $detail) { Get-Content -LiteralPath $detail -Raw -Encoding UTF8 }
    throw "Event Store self-test failed: $($process.ExitCode)"
}
$db = Join-Path $root '.claude-gui-v2\agent-store.db'
if (-not (Test-Path -LiteralPath $db)) { throw 'SQLite database missing' }
[pscustomobject]@{ SQLiteWAL = 'OK'; SchemaV11 = 'OK'; LegacyBackup = 'OK'; IdempotentMigration = 'OK'; DurableEvents = 'OK'; RequestId = 'OK'; ToolCallAudit = 'OK'; ContextLedger = 'OK'; WorkspaceMemory = 'OK'; ArtifactStore = 'OK'; ProviderHealthLedger = 'OK'; ProviderCooldownRecovery = 'OK'; ProviderSecretRedaction = 'OK'; ScheduleRunTracking = 'OK'; ScheduleLeaseRetryDeadLetter = 'OK'; DurableTaskFork = 'OK'; Database = $db } | Format-List

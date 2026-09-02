$ErrorActionPreference = 'Stop'
$statusScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'native-soak-status.ps1')).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-soak-status-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null

function Write-Fixture([string]$Name, [hashtable]$Value) {
    $path = Join-Path $root ($Name + '.json')
    [IO.File]::WriteAllText($path, ($Value | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    return $path
}

function Read-Status([string]$Path) {
    return (& $statusScript -ProgressPath $Path -AsJson | ConvertFrom-Json)
}

try {
    $now = [datetimeoffset]::Now
    $current = Get-Process -Id $PID
    $base = @{
        schemaVersion = 2
        state = 'running'
        monitorPid = $PID
        monitorStartedAt = $current.StartTime.ToString('o')
        monitorLeaseExpiresAt = $now.AddMinutes(1).ToString('o')
        currentHostPid = $PID
        samples = 7
        failures = 0
        updatedAt = $now.ToString('o')
    }

    $running = Read-Status (Write-Fixture 'running' $base)
    if ($running.state -ne 'running' -or -not $running.monitorAlive -or -not $running.hostAlive) { throw 'Live monitor was not recognized' }

    $stalledValue = $base.Clone()
    $stalledValue.monitorLeaseExpiresAt = $now.AddMinutes(-1).ToString('o')
    $stalled = Read-Status (Write-Fixture 'stalled' $stalledValue)
    if ($stalled.state -ne 'stalled' -or -not $stalled.monitorAlive) { throw 'Expired lease was not recognized' }

    $abandonedValue = $base.Clone()
    $abandonedValue.monitorPid = 2147483000
    $abandonedValue.currentHostPid = 2147483000
    $abandoned = Read-Status (Write-Fixture 'abandoned' $abandonedValue)
    if ($abandoned.state -ne 'abandoned' -or $abandoned.monitorAlive -or $abandoned.hostAlive) { throw 'Dead monitor was not recognized' }

    $passedValue = $base.Clone()
    $passedValue.state = 'passed'
    $passedValue.monitorPid = 0
    $passedValue.monitorLeaseExpiresAt = $null
    $passedValue.completedAt = $now.ToString('o')
    $passed = Read-Status (Write-Fixture 'passed' $passedValue)
    if ($passed.state -ne 'passed') { throw 'Terminal state was changed' }

    $missing = Read-Status (Join-Path $root 'missing.json')
    if ($missing.state -ne 'missing') { throw 'Missing progress file was not recognized' }

    [pscustomobject]@{
        NativeSoakStatus = 'PASS'
        Running = $running.state
        Stalled = $stalled.state
        Abandoned = $abandoned.state
        Terminal = $passed.state
        Missing = $missing.state
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

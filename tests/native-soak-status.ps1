param(
    [Parameter(Mandatory = $true)][string]$ProgressPath,
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
$ProgressPath = [IO.Path]::GetFullPath($ProgressPath)

function Test-ProcessIdentity([int]$ProcessId, [string]$ExpectedStartedAt) {
    if ($ProcessId -le 0) { return $false }
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    if ([string]::IsNullOrWhiteSpace($ExpectedStartedAt)) { return $true }
    try {
        $expected = [datetimeoffset]::Parse($ExpectedStartedAt)
        return [Math]::Abs((([datetimeoffset]$process.StartTime) - $expected).TotalSeconds) -lt 2
    } catch { return $false }
}

if (-not (Test-Path -LiteralPath $ProgressPath)) {
    $status = [ordered]@{ state = 'missing'; progressPath = $ProgressPath; monitorAlive = $false; hostAlive = $false }
} else {
    try { $progress = Get-Content -LiteralPath $ProgressPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch {
        $status = [ordered]@{ state = 'invalid'; progressPath = $ProgressPath; monitorAlive = $false; hostAlive = $false; message = $_.Exception.Message }
    }
    if ($null -ne $progress) {
        $monitorPid = 0
        $hostPid = 0
        [void][int]::TryParse([string]$progress.monitorPid, [ref]$monitorPid)
        [void][int]::TryParse([string]$progress.currentHostPid, [ref]$hostPid)
        $monitorAlive = Test-ProcessIdentity $monitorPid ([string]$progress.monitorStartedAt)
        $hostAlive = Test-ProcessIdentity $hostPid ''
        $effectiveState = [string]$progress.state
        $reason = ''
        if ($effectiveState -eq 'running' -and -not $monitorAlive) {
            $effectiveState = 'abandoned'
            $reason = if ($monitorPid -le 0) { '监控记录缺少有效 PID。' } else { '监控进程已退出或 PID 已被复用。' }
        } elseif ($effectiveState -eq 'running' -and $progress.monitorLeaseExpiresAt) {
            try {
                if ([datetimeoffset]::Parse([string]$progress.monitorLeaseExpiresAt) -lt [datetimeoffset]::Now) {
                    $effectiveState = 'stalled'
                    $reason = '监控进程仍存在，但心跳租约已过期。'
                }
            } catch {
                $effectiveState = 'invalid'
                $reason = '监控租约时间无效。'
            }
        }
        $status = [ordered]@{
            state = $effectiveState
            recordedState = [string]$progress.state
            message = if ($reason) { $reason } else { [string]$progress.message }
            schemaVersion = $progress.schemaVersion
            version = $progress.version
            sha256 = $progress.sha256
            startedAt = $progress.startedAt
            deadline = $progress.deadline
            updatedAt = $progress.updatedAt
            lastSampleAt = $progress.lastSampleAt
            completedAt = $progress.completedAt
            targetSamples = $progress.targetSamples
            remainingSamples = $progress.remainingSamples
            samples = $progress.samples
            failures = $progress.failures
            hostRestarts = $progress.hostRestarts
            transientFailures = $progress.transientFailures
            requestRetries = $progress.requestRetries
            suspendResumeCount = $progress.suspendResumeCount
            lastResumeAt = $progress.lastResumeAt
            lastSuccessfulRequestAt = $progress.lastSuccessfulRequestAt
            lastSuccessfulSampleAt = $progress.lastSuccessfulSampleAt
            logPath = $progress.logPath
            lastFailureEvidencePath = $progress.lastFailureEvidencePath
            monitorPid = $monitorPid
            monitorAlive = $monitorAlive
            currentHostPid = $hostPid
            hostAlive = $hostAlive
            progressPath = $ProgressPath
        }
    }
}

$result = [pscustomobject]$status
if ($AsJson) { $result | ConvertTo-Json -Depth 6 } else { $result }

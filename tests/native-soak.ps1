param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [double]$Hours = 72,
    [int]$SampleSeconds = 10,
    [int]$MaxPrivateMemoryMB = 900,
    [int]$MaxHandleGrowth = 1500,
    [int]$MaxRecoverySeconds = 30,
    [string]$AttachRuntime = '',
    [string]$ProgressPath = '',
    [string]$LogPath = '',
    [int]$SuspendGapSeconds = 30
)
$ErrorActionPreference = 'Stop'
if ($Hours -le 0 -or $Hours -gt 168) { throw 'Hours must be in (0, 168]' }
if ($SampleSeconds -lt 1 -or $SampleSeconds -gt 300) { throw 'SampleSeconds must be in [1, 300]' }
if ($MaxRecoverySeconds -lt 5 -or $MaxRecoverySeconds -gt 300) { throw 'MaxRecoverySeconds must be in [5, 300]' }
if ($SuspendGapSeconds -lt 2 -or $SuspendGapSeconds -gt 3600) { throw 'SuspendGapSeconds must be in [2, 3600]' }
$Executable = [IO.Path]::GetFullPath($Executable)
$attached = -not [string]::IsNullOrWhiteSpace($AttachRuntime)
$root = if ($attached) { Split-Path ([IO.Path]::GetFullPath($AttachRuntime)) -Parent | Split-Path -Parent } else { Join-Path ([IO.Path]::GetTempPath()) ('claude-soak-' + [guid]::NewGuid().ToString('N')) }
$root = [IO.Path]::GetFullPath($root)
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = if ($attached) { [IO.Path]::GetFullPath($AttachRuntime) } else { Join-Path $root '.claude-gui-v2\runtime-state.json' }
if ([string]::IsNullOrWhiteSpace($ProgressPath)) { $ProgressPath = Join-Path (Split-Path $runtimePath -Parent) 'soak-progress.json' }
$ProgressPath = [IO.Path]::GetFullPath($ProgressPath)
if ([string]::IsNullOrWhiteSpace($LogPath)) { $LogPath = $ProgressPath + '.events.jsonl' }
$LogPath = [IO.Path]::GetFullPath($LogPath)
$hostProcess = $null
$ownsHost = $false
$samples = 0
$failures = 0
$hostRestarts = 0
$peakMemory = 0L
$peakHandles = 0
$currentInitialHandles = 0
$currentPid = 0
$maximumOutageMs = 0L
$initialVersion = ''
$initialHash = ''
$startedAt = Get-Date
$targetSamples = [Math]::Max(1, [int][Math]::Ceiling($Hours * 3600 / $SampleSeconds))
$deadline = $startedAt.AddSeconds($targetSamples * $SampleSeconds)
$connection = $null
$monitorPid = $PID
$monitorStartedAt = [Diagnostics.Process]::GetCurrentProcess().StartTime
$monitorSessionId = [guid]::NewGuid().ToString('N')
$lastSampleAt = $null
$completedAt = $null
$leaseSeconds = [Math]::Max(30, [Math]::Min(600, $SampleSeconds * 4 + $MaxRecoverySeconds))
$lastObservationAt = $startedAt
$lastSuccessfulRequestAt = $null
$lastSuccessfulSampleAt = $null
$lastResumeAt = $null
$suspendResumeCount = 0
$transientFailures = 0
$requestRetries = 0
$consecutiveRequestFailures = 0
$lastFailureEvidencePath = ''

function Write-SoakEvent([string]$Type, [string]$Message = '', $Details = $null) {
    try {
        $directory = Split-Path $LogPath -Parent
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        $entry = [ordered]@{
            timestamp = (Get-Date).ToString('o')
            type = $Type
            message = $Message
            monitorPid = $monitorPid
            hostPid = $currentPid
            sample = $samples
            details = $Details
        }
        [IO.File]::AppendAllText($LogPath, (($entry | ConvertTo-Json -Depth 8 -Compress) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    } catch { }
}

function Observe-SuspendResume {
    $now = Get-Date
    $gapSeconds = ($now - $script:lastObservationAt).TotalSeconds
    $script:lastObservationAt = $now
    if ($gapSeconds -ge $SuspendGapSeconds) {
        $script:suspendResumeCount++
        $script:lastResumeAt = $now
        $script:deadline = $script:deadline.AddSeconds([Math]::Max(0, $gapSeconds - $SampleSeconds))
        Write-SoakEvent 'suspend-resume-gap' 'Detected a suspend, resume, or scheduler pause gap' ([ordered]@{ gapSeconds = [Math]::Round($gapSeconds, 3); thresholdSeconds = $SuspendGapSeconds })
        return $true
    }
    return $false
}

function Read-Runtime {
    try {
        if (-not (Test-Path -LiteralPath $runtimePath)) { return $null }
        return Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch { return $null }
}

function Get-Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('X2') }) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Use-Connection($Next, [bool]$CountRestart = $true) {
    if ($null -eq $Next) { throw 'Host connection is missing' }
    $nextPid = [int]$Next.Runtime.pid
    if ($CountRestart -and $script:currentPid -gt 0 -and $nextPid -ne $script:currentPid) { $script:hostRestarts++ }
    if ($script:initialVersion.Length -gt 0 -and [string]$Next.Runtime.appVersion -ne $script:initialVersion) {
        throw "Soak binary version changed from $script:initialVersion to $($Next.Runtime.appVersion)"
    }
    if ($script:initialHash.Length -gt 0 -and (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash -ne $script:initialHash) {
        throw 'Soak binary SHA-256 changed during validation'
    }
    $script:connection = $Next
    $script:currentPid = $nextPid
    $script:currentInitialHandles = [int]$Next.Process.HandleCount
}

function Invoke-HostProbe([string]$RelativePath, [string]$Label) {
    $expires = (Get-Date).AddSeconds($MaxRecoverySeconds)
    $attempt = 0
    do {
        $attempt++
        try {
            $result = Invoke-RestMethod -Uri ($script:connection.Base + $RelativePath) -Headers $script:connection.Headers -TimeoutSec 5
            $script:lastSuccessfulRequestAt = Get-Date
            if ($attempt -gt 1) { Write-SoakEvent 'probe-recovered' "$Label recovered within the bounded retry window" ([ordered]@{ attempts = $attempt; path = $RelativePath }) }
            $script:consecutiveRequestFailures = 0
            return $result
        }
        catch {
            $script:transientFailures++
            $script:consecutiveRequestFailures++
            $script:requestRetries++
            $wasResumeGap = Observe-SuspendResume
            Write-SoakEvent 'probe-retry' "$Label failed; retrying within the bounded recovery window" ([ordered]@{
                attempt = $attempt; path = $RelativePath; exceptionType = $_.Exception.GetType().FullName
                hresult = $_.Exception.HResult; error = $_.Exception.Message; suspendResumeGap = $wasResumeGap
            })
            if ((Get-Date) -ge $expires) { throw "$Label failed for $MaxRecoverySeconds seconds: $($_.Exception.Message)" }
            $outageStarted = Get-Date
            $next = Connect-Host ([datetime]::MinValue)
            if ($null -eq $next) { $next = Connect-Host ([datetime]::Now.AddSeconds([Math]::Min(3, $MaxRecoverySeconds))) }
            if ($null -ne $next) {
                $previousPid = $script:currentPid
                Use-Connection $next $true
                if ($script:currentPid -ne $previousPid) {
                    $outageMs = [long]((Get-Date) - $outageStarted).TotalMilliseconds
                    $script:maximumOutageMs = [Math]::Max($script:maximumOutageMs, $outageMs)
                    Write-SoakEvent 'host-reconnected' 'Host PID changed and the probe reconnected' ([ordered]@{ previousPid = $previousPid; currentPid = $script:currentPid; outageMs = $outageMs })
                }
            }
            Start-Sleep -Milliseconds ([Math]::Min(2000, 200 * [Math]::Pow(2, [Math]::Min(4, $attempt - 1))))
        }
    } while ($true)
}

function Save-FailureEvidence([Exception]$Error) {
    try {
        $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
        $evidenceRoot = Join-Path (Split-Path $ProgressPath -Parent) ('soak-evidence\' + $stamp + '-' + $monitorSessionId.Substring(0, 8))
        [IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
        $runtime = Read-Runtime
        $safeRuntime = if ($null -eq $runtime) { $null } else { [ordered]@{
            state = $runtime.state; pid = $runtime.pid; port = $runtime.port; appVersion = $runtime.appVersion; updatedAt = $runtime.updatedAt
        } }
        $failureEvidence = [ordered]@{
            capturedAt = (Get-Date).ToString('o'); exceptionType = $Error.GetType().FullName; hresult = $Error.HResult
            message = $Error.Message; stackTrace = $Error.StackTrace; innerMessage = if ($Error.InnerException) { $Error.InnerException.Message } else { '' }
            samples = $samples; transientFailures = $transientFailures; requestRetries = $requestRetries; consecutiveRequestFailures = $consecutiveRequestFailures
        }
        [IO.File]::WriteAllText((Join-Path $evidenceRoot 'failure.json'), ($failureEvidence | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $evidenceRoot 'runtime.sanitized.json'), ($safeRuntime | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        $process = Get-Process -Id $currentPid -ErrorAction SilentlyContinue
        $processEvidence = [ordered]@{ pid = $currentPid; alive = $null -ne $process; privateMemory = if ($process) { $process.PrivateMemorySize64 } else { $null }; handles = if ($process) { $process.HandleCount } else { $null } }
        [IO.File]::WriteAllText((Join-Path $evidenceRoot 'process.json'), ($processEvidence | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $ProgressPath) { [IO.File]::Copy($ProgressPath, (Join-Path $evidenceRoot 'progress-before-failure.json'), $true) }
        return $evidenceRoot
    } catch {
        Write-SoakEvent 'evidence-error' 'Failed to preserve failure evidence' ([ordered]@{ error = $_.Exception.Message })
        return ''
    }
}

function Connect-Host([datetime]$Expires) {
    do {
        $runtime = Read-Runtime
        if ($null -ne $runtime -and $runtime.state -eq 'running' -and [int]$runtime.pid -gt 0) {
            $process = Get-Process -Id ([int]$runtime.pid) -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                try {
                    Add-Type -AssemblyName System.Security
                    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
                    return @{
                        Runtime = $runtime
                        Process = $process
                        Base = "http://127.0.0.1:$($runtime.port)"
                        Headers = @{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}
                    }
                } catch { }
            }
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $Expires)
    return $null
}

function Write-Progress([string]$State, [string]$Message = '') {
    $now = Get-Date
    $terminal = $State -in @('passed', 'failed', 'cancelled', 'abandoned')
    if ($terminal -and $null -eq $script:completedAt) { $script:completedAt = $now }
    $value = [ordered]@{
        schemaVersion = 2
        state = $State
        message = $Message
        version = $initialVersion
        sha256 = $initialHash
        startedAt = $startedAt.ToString('o')
        deadline = $deadline.ToString('o')
        targetSamples = $targetSamples
        remainingSamples = [Math]::Max(0, $targetSamples - $samples)
        updatedAt = $now.ToString('o')
        lastSampleAt = if ($null -ne $script:lastSampleAt) { $script:lastSampleAt.ToString('o') } else { $null }
        completedAt = if ($null -ne $script:completedAt) { $script:completedAt.ToString('o') } else { $null }
        monitorPid = $monitorPid
        monitorStartedAt = $monitorStartedAt.ToString('o')
        monitorSessionId = $monitorSessionId
        monitorLeaseExpiresAt = if ($terminal) { $null } else { $now.AddSeconds($leaseSeconds).ToString('o') }
        attached = $attached
        logPath = $LogPath
        suspendResumeCount = $suspendResumeCount
        lastResumeAt = if ($null -ne $lastResumeAt) { $lastResumeAt.ToString('o') } else { $null }
        transientFailures = $transientFailures
        requestRetries = $requestRetries
        consecutiveRequestFailures = $consecutiveRequestFailures
        lastSuccessfulRequestAt = if ($null -ne $lastSuccessfulRequestAt) { $lastSuccessfulRequestAt.ToString('o') } else { $null }
        lastSuccessfulSampleAt = if ($null -ne $lastSuccessfulSampleAt) { $lastSuccessfulSampleAt.ToString('o') } else { $null }
        lastFailureEvidencePath = $lastFailureEvidencePath
        samples = $samples
        failures = $failures
        hostRestarts = $hostRestarts
        currentHostPid = $currentPid
        maximumOutageMs = $maximumOutageMs
        peakPrivateMemoryMB = [Math]::Round($peakMemory / 1MB, 1)
        peakHandles = $peakHandles
        progressPath = $ProgressPath
    }
    $directory = Split-Path $ProgressPath -Parent
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temp = $ProgressPath + '.tmp'
    [IO.File]::WriteAllText($temp, ($value | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $ProgressPath) {
        [IO.File]::Copy($temp, $ProgressPath, $true)
        [IO.File]::Delete($temp)
    } else { [IO.File]::Move($temp, $ProgressPath) }
}

try {
    if (-not $attached) {
        $env:CLAUDE_GUI_WORKSPACE = $root
        $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
        $env:CLAUDE_GUI_TEST_MODE = '1'
        $env:CLAUDE_GUI_MUTEX_SCOPE = 'soak-' + [guid]::NewGuid().ToString('N')
        $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
        $ownsHost = $true
    }

    Write-SoakEvent 'started' 'Background soak started' ([ordered]@{ hours = $Hours; sampleSeconds = $SampleSeconds; targetSamples = $targetSamples; attached = $attached })
    $connection = Connect-Host ((Get-Date).AddSeconds(25))
    if ($null -eq $connection) { throw 'Timed out waiting for initial Host' }
    $currentPid = [int]$connection.Runtime.pid
    $currentInitialHandles = [int]$connection.Process.HandleCount
    $initialVersion = [string]$connection.Runtime.appVersion
    $initialHash = Get-Sha256 $Executable
    Write-Progress 'running'

    while ($samples -lt $targetSamples) {
        $sampleStarted = Get-Date
        try {
            [void](Observe-SuspendResume)
            $runtime = Read-Runtime
            $needsReconnect = $null -eq $runtime -or $runtime.state -ne 'running' -or [int]$runtime.pid -ne $currentPid -or $null -eq (Get-Process -Id $currentPid -ErrorAction SilentlyContinue)
            if ($needsReconnect) {
                $outageStarted = Get-Date
                $next = Connect-Host ($outageStarted.AddSeconds($MaxRecoverySeconds))
                if ($null -eq $next) { throw "Host did not recover within $MaxRecoverySeconds seconds" }
                $outageMs = [long]((Get-Date) - $outageStarted).TotalMilliseconds
                $maximumOutageMs = [Math]::Max($maximumOutageMs, $outageMs)
                $previousPid = $currentPid
                Use-Connection $next $true
                Write-SoakEvent 'host-reconnected' 'Host recovered and the probe reconnected' ([ordered]@{ previousPid = $previousPid; currentPid = $currentPid; outageMs = $outageMs })
            }

            $bootstrap = Invoke-HostProbe '/api/bootstrap' 'Bootstrap'
            $health = Invoke-HostProbe ('/api/workbench/health?workspace=' + [uri]::EscapeDataString($root)) 'Health'
            if ($bootstrap.persistence.integrity -ne 'ok' -or -not $health.durableJobState) { throw 'Health invariant failed' }
            $process = Get-Process -Id $currentPid -ErrorAction Stop
            $memory = [long]$process.PrivateMemorySize64
            $handles = [int]$process.HandleCount
            $peakMemory = [Math]::Max($peakMemory, $memory)
            $peakHandles = [Math]::Max($peakHandles, $handles)
            if ($memory -gt $MaxPrivateMemoryMB * 1MB) { throw "Private memory exceeded $MaxPrivateMemoryMB MB" }
            if (($handles - $currentInitialHandles) -gt $MaxHandleGrowth) { throw "Handle growth exceeded $MaxHandleGrowth" }
            $samples++
            $lastSampleAt = Get-Date
            $lastSuccessfulSampleAt = $lastSampleAt
            Write-Progress 'running'
        }
        catch { throw }
        $remainingDelay = [Math]::Max(0, $SampleSeconds * 1000 - [int]((Get-Date) - $sampleStarted).TotalMilliseconds)
        if ($remainingDelay -gt 0) { Start-Sleep -Milliseconds $remainingDelay }
    }

    Write-Progress 'passed'
    Write-SoakEvent 'passed' 'Background soak passed' ([ordered]@{ samples = $samples; transientFailures = $transientFailures; requestRetries = $requestRetries; suspendResumeCount = $suspendResumeCount })
    [pscustomobject]@{
        Soak = 'PASS'
        RequestedHours = $Hours
        Samples = $samples
        Failures = $failures
        HostRestarts = $hostRestarts
        MaximumOutageMs = $maximumOutageMs
        Version = $initialVersion
        SHA256 = $initialHash
        PeakPrivateMemoryMB = [Math]::Round($peakMemory / 1MB, 1)
        PeakHandles = $peakHandles
        SQLiteIntegrity = 'OK'
        Root = $root
        ProgressPath = $ProgressPath
    } | Format-List
}
catch {
    if ($failures -eq 0) { $failures++ }
    $lastFailureEvidencePath = Save-FailureEvidence $_.Exception
    Write-Progress 'failed' $_.Exception.Message
    Write-SoakEvent 'failed' 'Background soak failed' ([ordered]@{ error = $_.Exception.Message; evidencePath = $lastFailureEvidencePath })
    throw
}
finally {
    if ($ownsHost -and $hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if (-not $attached) { Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue }
}

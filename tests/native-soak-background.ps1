param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [double]$Hours = 72,
    [int]$SampleSeconds = 10,
    [int]$MaxPrivateMemoryMB = 900,
    [int]$MaxHandleGrowth = 1500,
    [int]$MaxRecoverySeconds = 30,
    [string]$AttachRuntime = '',
    [string]$LogPath = '',
    [int]$SuspendGapSeconds = 30,
    [Parameter(Mandatory = $true)][string]$ProgressPath
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$ProgressPath = [IO.Path]::GetFullPath($ProgressPath)
$runnerScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'native-soak-task-runner.ps1')).Path
$statusScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'native-soak-status.ps1')).Path

function Quote-CommandLineArgument([string]$Value) {
    return '"' + $Value.Replace('\', '\').Replace('"', '\"') + '"'
}

function Read-SoakStatus {
    if (-not (Test-Path -LiteralPath $ProgressPath)) { return $null }
    return (& $statusScript -ProgressPath $ProgressPath -AsJson | ConvertFrom-Json)
}

$existing = Read-SoakStatus
if ($null -ne $existing -and $existing.state -eq 'running') {
    [pscustomobject]@{
        BackgroundSoak = 'ALREADY_RUNNING'
        MonitorPid = [int]$existing.monitorPid
        ProgressPath = $ProgressPath
        StartedAt = $existing.startedAt
        Deadline = $existing.deadline
    } | Format-List
    exit 0
}

if (Test-Path -LiteralPath $ProgressPath) {
    $archive = $ProgressPath + '.' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.' + $(if ($null -ne $existing) { $existing.state } else { 'unknown' }) + '.json'
    [IO.File]::Move($ProgressPath, $archive)
}

$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $pathHash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ProgressPath.ToLowerInvariant())) | ForEach-Object { $_.ToString('x2') })
} finally { $sha.Dispose() }
$taskName = 'ClaudeCodeWorkbench-Soak-' + $pathHash.Substring(0, 12)
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
$parts = [Collections.Generic.List[string]]::new()
$parts.Add('-NoProfile')
$parts.Add('-NonInteractive')
$parts.Add('-ExecutionPolicy Bypass')
$parts.Add('-WindowStyle Hidden')
$parts.Add('-File ' + (Quote-CommandLineArgument $runnerScript))
$parts.Add('-ScheduledTaskName ' + (Quote-CommandLineArgument $taskName))
$parts.Add('-Executable ' + (Quote-CommandLineArgument $Executable))
$parts.Add('-Hours ' + $Hours.ToString([Globalization.CultureInfo]::InvariantCulture))
$parts.Add('-SampleSeconds ' + $SampleSeconds)
$parts.Add('-MaxPrivateMemoryMB ' + $MaxPrivateMemoryMB)
$parts.Add('-MaxHandleGrowth ' + $MaxHandleGrowth)
$parts.Add('-MaxRecoverySeconds ' + $MaxRecoverySeconds)
$parts.Add('-ProgressPath ' + (Quote-CommandLineArgument $ProgressPath))
$parts.Add('-SuspendGapSeconds ' + $SuspendGapSeconds)
if (-not [string]::IsNullOrWhiteSpace($LogPath)) { $parts.Add('-LogPath ' + (Quote-CommandLineArgument ([IO.Path]::GetFullPath($LogPath)))) }
if (-not [string]::IsNullOrWhiteSpace($AttachRuntime)) {
    $parts.Add('-AttachRuntime ' + (Quote-CommandLineArgument ([IO.Path]::GetFullPath($AttachRuntime))))
}
$commandLine = $parts -join ' '

# Task Scheduler owns the process tree, so it survives the Codex command
# session and does not consume the active conversation's execution process.
$action = New-ScheduledTaskAction -Execute $powershell -Argument $commandLine -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([timespan]::FromHours([Math]::Ceiling($Hours * 2 + 6))) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description 'ClaudeCodeWorkbench isolated background soak validation' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

$expires = (Get-Date).AddSeconds(25)
do {
    Start-Sleep -Milliseconds 200
    $status = Read-SoakStatus
    if ($null -ne $status -and [int]$status.monitorPid -gt 0) {
        if ($status.state -ne 'running') { throw "Detached soak entered $($status.state): $($status.message)" }
        [pscustomobject]@{
            BackgroundSoak = 'STARTED'
            MonitorPid = [int]$status.monitorPid
            ScheduledTask = $taskName
            ProgressPath = $ProgressPath
            StartedAt = $status.startedAt
            Deadline = $status.deadline
        } | Format-List
        exit 0
    }
} while ((Get-Date) -lt $expires)

throw "Scheduled soak task $taskName did not publish progress within 25 seconds"

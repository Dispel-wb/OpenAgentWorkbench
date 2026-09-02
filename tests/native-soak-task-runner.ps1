param(
    [Parameter(Mandatory = $true)][string]$ScheduledTaskName,
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
$soakScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'native-soak.ps1')).Path
$arguments = @{
    Executable = $Executable
    Hours = $Hours
    SampleSeconds = $SampleSeconds
    MaxPrivateMemoryMB = $MaxPrivateMemoryMB
    MaxHandleGrowth = $MaxHandleGrowth
    MaxRecoverySeconds = $MaxRecoverySeconds
    SuspendGapSeconds = $SuspendGapSeconds
    ProgressPath = $ProgressPath
}
if (-not [string]::IsNullOrWhiteSpace($AttachRuntime)) { $arguments.AttachRuntime = $AttachRuntime }
if (-not [string]::IsNullOrWhiteSpace($LogPath)) { $arguments.LogPath = $LogPath }

try { & $soakScript @arguments }
catch {
    try {
        $runnerErrorPath = [IO.Path]::GetFullPath($ProgressPath) + '.runner-error.json'
        $runnerError = [ordered]@{
            state = 'runner-failed'
            capturedAt = (Get-Date).ToString('o')
            exceptionType = $_.Exception.GetType().FullName
            hresult = $_.Exception.HResult
            message = $_.Exception.Message
            scriptStackTrace = $_.ScriptStackTrace
        }
        [IO.File]::WriteAllText($runnerErrorPath, ($runnerError | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    } catch { }
    throw
}
finally {
    # Removing the definition does not stop the already-running task instance.
    Unregister-ScheduledTask -TaskName $ScheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue
}

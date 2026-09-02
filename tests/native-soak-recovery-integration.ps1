param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$soakScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'native-soak.ps1')).Path
$powershell = (Get-Process -Id $PID).Path
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-soak-recovery-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not ($root.TrimEnd('\') + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe soak recovery root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$progressPath = Join-Path $root '.claude-gui-v2\soak-progress.json'
$hostProcess = $null
$soakProcess = $null
$watchdogProcess = $null

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds, [string]$Message) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = & $Action } catch { $value = $null }
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Message"
}

function Quote([string]$Value) { return '"' + $Value.Replace('"', '\"') + '"' }

try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_WATCHDOG_TEST = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'soak-recovery-' + [guid]::NewGuid().ToString('N')
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $initialRuntime = Wait-Until {
        if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -eq $hostProcess.Id } 25 'initial Host'
    $watchdogStatePath = Join-Path $root '.claude-gui-v2\watchdog-state.json'
    $watchdogState = Wait-Until {
        if (Test-Path -LiteralPath $watchdogStatePath) { Get-Content -LiteralPath $watchdogStatePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'watching' -and [int]$value.pid -gt 0 } 20 'Watchdog'
    $watchdogProcess = Get-Process -Id ([int]$watchdogState.pid) -ErrorAction Stop

    $arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (Quote $soakScript) +
        ' -Executable ' + (Quote $Executable) + ' -Hours 0.008 -SampleSeconds 1 -MaxRecoverySeconds 12 -AttachRuntime ' +
        (Quote $runtimePath) + ' -ProgressPath ' + (Quote $progressPath)
    $soakProcess = Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $running = Wait-Until {
        if (Test-Path -LiteralPath $progressPath) { Get-Content -LiteralPath $progressPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.samples -ge 2 -and [int]$value.monitorPid -eq $soakProcess.Id -and $value.monitorLeaseExpiresAt } 20 'initial soak samples'

    Stop-Process -Id $hostProcess.Id -Force -ErrorAction Stop
    $hostProcess.WaitForExit(8000) | Out-Null
    $recoveredRuntime = Wait-Until {
        if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -ne $hostProcess.Id -and $null -ne (Get-Process -Id ([int]$value.pid) -ErrorAction SilentlyContinue) } 30 'recovered Host'

    if (-not $soakProcess.WaitForExit(45 * 1000)) { throw 'Recovery soak did not finish' }
    if ($soakProcess.ExitCode -ne 0) { throw "Recovery soak failed with exit code $($soakProcess.ExitCode)" }
    $final = Get-Content -LiteralPath $progressPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($final.state -ne 'passed' -or [int]$final.hostRestarts -lt 1 -or [long]$final.maximumOutageMs -le 0 -or -not $final.completedAt -or $final.monitorLeaseExpiresAt) { throw 'Recovery evidence is incomplete' }

    [pscustomobject]@{
        NativeSoakRecovery = 'PASS'
        InitialHostPid = $hostProcess.Id
        RecoveredHostPid = [int]$recoveredRuntime.pid
        Samples = [int]$final.samples
        HostRestarts = [int]$final.hostRestarts
        MaximumOutageMs = [long]$final.maximumOutageMs
        SQLiteIntegrity = 'ok'
    } | Format-List
}
finally {
    try {
        $stop = Start-Process -FilePath $Executable -ArgumentList '--stop-watchdog' -WorkingDirectory $root -WindowStyle Hidden -PassThru
        $stop.WaitForExit(5000) | Out-Null
    } catch { }
    if ($soakProcess -and -not $soakProcess.HasExited) { Stop-Process -Id $soakProcess.Id -Force -ErrorAction SilentlyContinue }
    $runtime = try { if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $null }
    if ($runtime -and [int]$runtime.pid -gt 0) { Stop-Process -Id ([int]$runtime.pid) -Force -ErrorAction SilentlyContinue }
    if ($watchdogProcess -and -not $watchdogProcess.HasExited) { Stop-Process -Id $watchdogProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_WATCHDOG_TEST,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

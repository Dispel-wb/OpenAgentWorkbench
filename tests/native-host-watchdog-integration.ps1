param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-watchdog-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not ($root.TrimEnd('\') + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe watchdog test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$watchdogPath = Join-Path $root '.claude-gui-v2\watchdog-state.json'
$hostProcess = $null
$recoveredHost = $null
$watchdog = $null

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25, [string]$Message = 'condition') {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = & $Action } catch { $value = $null }
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Message"
}

try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_WATCHDOG_TEST = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'watchdog-' + [guid]::NewGuid().ToString('N')

    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $initialRuntime = Wait-Until {
        if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -eq $hostProcess.Id } 25 'initial Host'
    $initialWatchdog = Wait-Until {
        if (Test-Path -LiteralPath $watchdogPath) { Get-Content -LiteralPath $watchdogPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and [int]$value.pid -gt 0 -and $value.state -eq 'watching' } 20 'Watchdog startup'
    $watchdog = Get-Process -Id ([int]$initialWatchdog.pid) -ErrorAction Stop

    $hostPids = @($hostProcess.Id)
    $currentHost = $hostProcess
    $recoveredRuntime = $initialRuntime
    1..3 | ForEach-Object {
        $cycle = $_
        Stop-Process -Id $currentHost.Id -Force -ErrorAction Stop
        $currentHost.WaitForExit(8000) | Out-Null
        $recoveredRuntime = Wait-Until {
            if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json }
        } { param($value)
            if ($null -eq $value -or $value.state -ne 'running' -or $hostPids -contains [int]$value.pid) { return $false }
            return $null -ne (Get-Process -Id ([int]$value.pid) -ErrorAction SilentlyContinue)
        } 35 "Watchdog Host recovery cycle $cycle"
        $currentHost = Get-Process -Id ([int]$recoveredRuntime.pid) -ErrorAction Stop
        $hostPids += $currentHost.Id
        if ($cycle -lt 3) { Start-Sleep -Milliseconds 500 }
    }
    $recoveredHost = $currentHost

    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$recoveredRuntime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $headers = @{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}
    $bootstrap = Invoke-RestMethod -Uri "http://127.0.0.1:$($recoveredRuntime.port)/api/bootstrap" -Headers $headers -TimeoutSec 8
    $finalWatchdog = Wait-Until {
        if (Test-Path -LiteralPath $watchdogPath) { Get-Content -LiteralPath $watchdogPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and [int]$value.pid -eq $watchdog.Id -and [int]$value.hostPid -eq $recoveredHost.Id -and [int]$value.restartCount -ge 3 -and $value.state -eq 'watching' } 20 'Watchdog recovery evidence'

    $stop = Start-Process -FilePath $Executable -ArgumentList '--stop-watchdog' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $stop.WaitForExit(8000) | Out-Null
    if (-not $watchdog.WaitForExit(8000)) { throw 'Watchdog ignored maintenance stop signal' }

    [pscustomobject]@{
        NativeHostWatchdog = 'PASS'
        InitialHostPid = $hostProcess.Id
        RecoveredHostPid = $recoveredHost.Id
        HostPidSequence = $hostPids -join ' -> '
        WatchdogPid = $watchdog.Id
        RestartCount = [int]$finalWatchdog.restartCount
        BootstrapVersion = $bootstrap.version
        SQLiteIntegrity = $bootstrap.persistence.integrity
        MaintenanceStop = $watchdog.HasExited
    } | Format-List
}
finally {
    try {
        $signal = Start-Process -FilePath $Executable -ArgumentList '--stop-watchdog' -WorkingDirectory $root -WindowStyle Hidden -PassThru
        $signal.WaitForExit(5000) | Out-Null
    } catch { }
    foreach ($process in @($recoveredHost, $hostProcess, $watchdog)) {
        if ($null -ne $process) { try { if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } } catch { } }
    }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_WATCHDOG_TEST,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

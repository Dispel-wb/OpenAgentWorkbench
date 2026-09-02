param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateRange(1, 8)][int]$RendererCrashCount = 3
)

$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('claude-local-ui-recovery-' + [guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$executablePath = [IO.Path]::GetFullPath($Executable)
$runtimePath = Join-Path $testRoot '.claude-gui-v2\runtime-state.json'
$uiStatePath = Join-Path $testRoot '.claude-gui-v2\ui-connection-state.json'
$hostProcess = $null
$replacementHost = $null
$ui = $null

function Wait-State([string]$Path, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 150
        try { $value = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $value = $null }
        if ($null -ne $value -and (& $Predicate $value)) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Path"
}

try {
    $env:CLAUDE_GUI_WORKSPACE = $testRoot
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'ui-recovery-' + [guid]::NewGuid().ToString('N')
    $hostProcess = Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $firstRuntime = Wait-State $runtimePath { param($state) $state.state -eq 'running' -and [int]$state.pid -eq $hostProcess.Id }
    $ui = Start-Process -FilePath $executablePath -ArgumentList '--ui' -WorkingDirectory $testRoot -PassThru
    $firstUiState = Wait-State $uiStatePath { param($state) [int]$state.hostPid -eq [int]$firstRuntime.pid }

    & (Join-Path $PSScriptRoot 'ui-resilience.ps1') -RuntimePath $runtimePath -UiPid $ui.Id -Iterations 80 `
        -RendererCrashCount $RendererCrashCount -Output (Join-Path $testRoot 'recovery-final.png')

    Stop-Process -Id $hostProcess.Id -Force
    $hostProcess.WaitForExit(8000) | Out-Null
    Start-Sleep -Seconds 3
    $ui.Refresh()
    if ($ui.HasExited -or -not $ui.Responding) { throw 'UI did not survive Host outage' }

    $replacementHost = Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $secondRuntime = Wait-State $runtimePath { param($state) $state.state -eq 'running' -and [int]$state.pid -eq $replacementHost.Id }
    $secondUiState = Wait-State $uiStatePath { param($state) $state.state -eq 'connected' -and [int]$state.hostPid -eq [int]$secondRuntime.pid }
    $ui.Refresh()
    if ($ui.HasExited -or -not $ui.Responding) { throw 'UI did not recover after Host replacement' }
    if ([int]$firstUiState.uiPid -ne [int]$secondUiState.uiPid) { throw 'UI process was replaced during Host recovery' }

    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$secondRuntime.authProtected), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers = @{'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2'}
    $metrics = Invoke-RestMethod -Uri "http://127.0.0.1:$($secondRuntime.port)/api/workbench/metrics" -Headers $headers -TimeoutSec 8
    if ([int]$metrics.host.uiReconnects -lt 1) { throw 'UI reconnect metrics were not recorded' }

    [pscustomobject]@{
        UiRecoveryIntegration = 'PASS'
        RendererCrashesRecovered = $RendererCrashCount
        UiPid = [int]$secondUiState.uiPid
        UiPidUnchanged = $true
        FirstHostPid = [int]$firstRuntime.pid
        ReplacementHostPid = [int]$secondRuntime.pid
        HostReplacementRecovered = $true
        UiReconnectMetrics = [int]$metrics.host.uiReconnects
    } | Format-List
}
finally {
    foreach ($process in @($ui, $hostProcess, $replacementHost)) {
        if ($null -ne $process) {
            try {
                $process.Refresh()
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
                $process.WaitForExit(5000) | Out-Null
            } catch { }
        }
    }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $testRoot) -and $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        for ($attempt = 0; $attempt -lt 12 -and (Test-Path -LiteralPath $testRoot); $attempt++) {
            Start-Sleep -Milliseconds 300
            Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $testRoot) { Write-Warning "Deferred cleanup required: $testRoot" }
    }
}

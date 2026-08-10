param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Workspace
)
$ErrorActionPreference = 'Stop'
function Wait-State([string]$Path, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 150
        $value = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        if ($null -ne $value -and (& $Predicate $value)) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Path"
}
$env:CLAUDE_GUI_WORKSPACE = $Workspace
$runtimePath = Join-Path $Workspace '.claude-gui-v2\runtime-state.json'
$uiStatePath = Join-Path $Workspace '.claude-gui-v2\ui-connection-state.json'
$firstHost = Wait-State $runtimePath { param($s) $s.state -eq 'running' }
$ui = Start-Process -FilePath $Executable -ArgumentList '--ui' -WorkingDirectory $Workspace -PassThru
$firstUi = Wait-State $uiStatePath { param($s) [int]$s.hostPid -eq [int]$firstHost.pid }
Stop-Process -Id ([int]$firstHost.pid) -Force
Start-Sleep -Milliseconds 700
$newHostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $Workspace -WindowStyle Hidden -PassThru
$secondHost = Wait-State $runtimePath { param($s) $s.state -eq 'running' -and [int]$s.pid -ne [int]$firstHost.pid }
$secondUi = Wait-State $uiStatePath { param($s) [int]$s.hostPid -eq [int]$secondHost.pid }
$ui.Refresh()
if ($ui.HasExited) { throw 'UI exited while Host was restarted' }
if ([int]$firstUi.uiPid -ne [int]$secondUi.uiPid) { throw 'UI process was replaced instead of reconnecting' }
$ui.CloseMainWindow() | Out-Null
if (-not $ui.WaitForExit(8000)) { Stop-Process -Id $ui.Id -Force }
[pscustomobject]@{
    UiPid = $secondUi.uiPid
    OldHostPid = $firstHost.pid
    NewHostPid = $secondHost.pid
    UiPidUnchanged = [int]$firstUi.uiPid -eq [int]$secondUi.uiPid
    Protocol = $secondUi.protocolVersion
    Reconnected = $true
} | Format-List

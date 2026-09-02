$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$native = Get-Content -LiteralPath (Join-Path $root 'native\NativeHost.cs') -Raw -Encoding UTF8
$app = Get-Content -LiteralPath (Join-Path $root 'static\app.js') -Raw -Encoding UTF8
$html = Get-Content -LiteralPath (Join-Path $root 'static\index.html') -Raw -Encoding UTF8

function Assert-Contains([string]$Value, [string]$Expected, [string]$Message) {
    if (-not $Value.Contains($Expected)) { throw $Message }
}

Assert-Contains $native 'CheckHostConnectionAsync' 'Native Host liveness watchdog is missing'
Assert-Contains $native 'ScheduleBrowserRecovery("' 'Navigation failures do not trigger recovery'
Assert-Contains $native 'e.WebErrorStatus' 'Navigation failure status is not preserved'
Assert-Contains $native 'ValidateBrowserContentAsync' 'Blank DOM watchdog is missing'
Assert-Contains $native 'NativeMetrics.RecordUiReconnect()' 'UI reconnect metrics are not recorded'
Assert-Contains $native 'if (_recoveryPanel != null)' 'Recovery panel deduplication is missing'
Assert-Contains $app 'if(error.httpStatus&&this.pollFailures>=5)' 'HTTP failure policy is missing'
if ($app.Contains('if(this.pollFailures>=5)await this.finishChat')) {
    throw 'Transport failures still terminally fail active Runs after five polls'
}
Assert-Contains $app 'else this.status=`Agent Host' 'Durable reconnect status is missing'
Assert-Contains $html "connectionState==='reconnecting'" 'Visible reconnect banner is missing'

[pscustomobject]@{
    UiRecoveryContract = 'PASS'
    HostLiveness = $true
    NavigationRecovery = $true
    BlankDomWatchdog = $true
    DurableRunPolling = $true
    VisibleReconnectState = $true
} | Format-List

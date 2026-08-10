param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$RuntimeFile = 'D:\work\Claude\.claude-gui-v2\runtime-state.json'
)

$ErrorActionPreference = 'Stop'
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "PROCESS SPLIT FAILED: $Message" }
}

$before = @(Get-Process ClaudeCodeWorkbench -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$ui = Start-Process -FilePath $Executable -ArgumentList '--ui' -PassThru
$expires = (Get-Date).AddSeconds(20)
do {
    Start-Sleep -Milliseconds 200
    $runtime = if (Test-Path -LiteralPath $RuntimeFile) { Get-Content -Raw -LiteralPath $RuntimeFile -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $hostProcess = if ($runtime) { Get-Process -Id ([int]$runtime.pid) -ErrorAction SilentlyContinue } else { $null }
} until (($hostProcess -and -not $ui.HasExited -and $hostProcess.Id -ne $ui.Id) -or (Get-Date) -gt $expires)

Assert-True ($null -ne $hostProcess) 'Host process was not started'
Assert-True (-not $ui.HasExited) 'UI process exited during startup'
Assert-True ($hostProcess.Id -ne $ui.Id) 'UI and Host are still the same process'

$ui.CloseMainWindow() | Out-Null
if (-not $ui.WaitForExit(8000)) { Stop-Process -Id $ui.Id -Force; throw 'PROCESS SPLIT FAILED: UI did not close' }
Start-Sleep -Milliseconds 500
$hostAfterUiClose = Get-Process -Id $hostProcess.Id -ErrorAction SilentlyContinue
Assert-True ($null -ne $hostAfterUiClose) 'Host exited when UI closed'

$baseUrl = "http://127.0.0.1:$($runtime.port)"
$page = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/"
Assert-True ($page.StatusCode -eq 200) 'Host HTTP API stopped after UI closed'

[pscustomobject]@{
    UiPid = $ui.Id
    HostPid = $hostProcess.Id
    UiExited = $ui.HasExited
    HostAliveAfterUiClose = $null -ne $hostAfterUiClose
    ApiAliveAfterUiClose = $page.StatusCode -eq 200
} | Format-List

# The assertion above deliberately proves that Host survived UI shutdown.  Release
# this test-owned Host afterwards so later isolated lifecycle tests can acquire the
# single-instance mutex.
Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue

param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
Add-Type -AssemblyName System.Net.Http

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw ('Timed out waiting for background reconciliation: ' + [string]$script:stage)
}
function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 14 -Compress)) }
    Invoke-RestMethod @request
}
function Start-FixtureRun($Connection, [string]$Workspace, [string]$Prompt) {
    $session = [guid]::NewGuid().ToString()
    Api $Connection '/api/chat/start' 'POST' @{
        workspace=$Workspace;prompt=$Prompt;sessionId=$session;claudeSessionId=$session;resume=$false;requestId=('background:'+[guid]::NewGuid().ToString('N'))
        providerId='offline-background';model='offline-model';effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()
    }
}
function Wait-HostTerminal($Connection, [string]$RuntimePath, [string]$RunId) {
    $script:stage="Host convergence for $RunId"
    Wait-Until {
        $runtime = Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrap = Api $Connection '/api/bootstrap'
        [pscustomobject]@{Runtime=$runtime;Bootstrap=$bootstrap}
    } {
        param($value)
        $null -ne $value -and [int]$value.Runtime.activeJobs -eq 0 -and @($value.Bootstrap.activeJobs | Where-Object id -eq $RunId).Count -eq 0
    }
}
function Stop-TestUi([string]$Root,[string]$ExecutablePath) {
    $statePath=Join-Path $Root '.claude-gui-v2\ui-connection-state.json'
    if(-not(Test-Path -LiteralPath $statePath)){return}
    try {
        $state=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8|ConvertFrom-Json
        $uiPid=[int]$state.uiPid
        $ui=Get-CimInstance Win32_Process -Filter "ProcessId=$uiPid" -ErrorAction SilentlyContinue
        if($null-ne$ui-and[string]::Equals([string]$ui.ExecutablePath,$ExecutablePath,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $uiPid -Force -ErrorAction SilentlyContinue}
    } catch { }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-background-reconcile-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1')
$csc = $testDependencies.Compiler
$json = $testDependencies.Json
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

$env:CLAUDE_GUI_WORKSPACE=$root
$env:CLAUDE_GUI_ROOT=(Join-Path $root 'empty-install')
$env:CLAUDE_GUI_CLAUDE_EXE=$fake
$env:CLAUDE_GUI_TEST_MODE='1'
$env:CLAUDE_GUI_MUTEX_SCOPE='background-reconcile-'+[guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{
        id='offline-background';name='Offline Background';token='background-secret';authStyle='bearer'
        text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | Out-Null

    $success = Start-FixtureRun $connection $root 'background-terminal-success'
    $successConvergence = Wait-HostTerminal $connection $runtimePath $success.jobId
    $successState = Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($success.jobId)\job-state.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($successState.state -ne 'completed' -or $successState.details.reconciledByHost -ne $true) { throw 'Successful Run was not finalized by the Host supervisor' }
    $successPoll = Api $connection "/api/chat/poll/$($success.jobId)?after=0"
    if ($successPoll.status.state -ne 'completed' -or -not (@($successPoll.events.payload) -join "`n").Contains('background-terminal-success')) { throw 'Completed background Run could not be read after convergence' }

    Api $connection '/api/providers' 'POST' @{
        id='offline-fallback';name='Offline Fallback';token='fallback-fixture';authStyle='bearer'
        text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('fallback-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | Out-Null
    $failed = Start-FixtureRun $connection $root 'provider-auth-failure'
    $failedConvergence = Wait-HostTerminal $connection $runtimePath $failed.jobId
    $failedStatus = Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($failed.jobId)\status.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $failedState = Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($failed.jobId)\job-state.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($failedState.state -ne 'failed' -or $failedState.details.reconciledByHost -ne $true) { throw 'Failed Run was not finalized by the Host supervisor' }
    if (-not $failedStatus.providerHealthRecorded -or $null -eq $failedStatus.fallbackDecision) {
        throw ('Background failure did not retain Provider evidence and fallback decision: ' + ($failedStatus | Select-Object state,worker,harness,providerHealthRecorded,providerHealthSkipped,failureClassification | ConvertTo-Json -Depth 6 -Compress))
    }
    if ($failedStatus.fallbackDecision.automatic -ne $false -or @($failedStatus.fallbackDecision.candidates | Where-Object providerId -eq 'offline-fallback').Count -ne 1) { throw 'Explicit fallback fixture was missing or selected without user confirmation' }
    $failureJson = $failedStatus | ConvertTo-Json -Depth 16 -Compress
    if ($failureJson.Contains('sk-provider-health-secret-value') -or $failureJson.Contains('background-secret')) { throw 'Background terminal evidence leaked a secret' }
    $metrics = Api $connection '/api/workbench/metrics'
    if ([long]$metrics.runs.started -lt 2 -or [long]$metrics.runs.completed -lt 1 -or [long]$metrics.runs.failed -lt 1 -or [long]$metrics.runs.active -ne 0) { throw 'Run lifecycle metrics did not converge with background jobs' }
    if ([long]$metrics.runs.durationSumMs -le 0 -or [long]$metrics.runs.p50Ms -le 0 -or @($metrics.runs.activeRunIds).Count -ne 0) { throw 'Run duration or active-ID metrics did not converge' }

    [pscustomobject]@{
        BackgroundJobReconciliation='PASS';NoUiPollBeforeTerminal=$true;SuccessfulRun=$success.jobId;SuccessfulState=$successState.state
        FailedRun=$failed.jobId;FailedState=$failedState.state;RuntimeActiveJobs=[int]$failedConvergence.Runtime.activeJobs
        ProviderEvidence='persisted-redacted';RunMetrics="started=$($metrics.runs.started),completed=$($metrics.runs.completed),failed=$($metrics.runs.failed)";WorkbenchProcess=$connection.Runtime.pid;Workspace=$root
    } | Format-List
}
catch {
    $logPath = Join-Path $root '.claude-gui-v2/native-runtime.log'
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 60 | Write-Output }
    throw
}
finally {
    Stop-TestUi $root $Executable
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

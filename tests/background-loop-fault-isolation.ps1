param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for isolated background loop'
}
function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress)) }
    Invoke-RestMethod @request
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-background-loop-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe background loop fixture root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$env:CLAUDE_GUI_WORKSPACE=$root
$env:CLAUDE_GUI_ROOT=(Join-Path $root 'empty-install')
$env:CLAUDE_GUI_TEST_MODE='1'
$env:CLAUDE_GUI_TEST_FAIL_BACKGROUND_LOOPS='queue,job'
$env:CLAUDE_GUI_MUTEX_SCOPE='background-loop-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess=$null
try {
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection=Connect-Host $runtimePath
    $pidBefore=[int]$connection.Runtime.pid
    $runtimeLog=Join-Path $root '.claude-gui-v2\native-runtime.log'
    $evidence=Wait-Until { if(Test-Path -LiteralPath $runtimeLog){Get-Content -LiteralPath $runtimeLog -Raw -Encoding UTF8} } { param($value) $value -and $value.Contains('[handled:TaskQueueLoop]') -and $value.Contains('[handled:JobReconcileLoop]') }
    $session=[guid]::NewGuid().ToString()
    Api $connection '/api/task-queue' 'POST' @{sessionId=$session;text='后台循环恢复验证';kind='queued';request=@{workspace=$root;providerId='missing';model='offline';permissionMode='readonly'}} | Out-Null
    $queue=Wait-Until { Api $connection ("/api/task-queue?sessionId="+[uri]::EscapeDataString($session)+"&history=1") } { param($value) @($value|Where-Object state -eq 'failed').Count -eq 1 }
    $runtimeAfter=Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json
    $metrics=Api $connection '/api/workbench/metrics'
    if([int]$runtimeAfter.pid-ne$pidBefore-or$runtimeAfter.state-ne'running'-or-not(Get-Process -Id $pidBefore -ErrorAction SilentlyContinue)){throw 'A background loop exception terminated or replaced the Host'}
    if([long]$metrics.host.currentSession.crashes-ne0){throw 'Handled background loop faults polluted the Host crash counter'}
    if([long]$metrics.host.currentSession.handledErrors-lt3){throw 'Handled background errors were not recorded separately'}
    [pscustomobject]@{BackgroundLoopFaultIsolation='PASS';QueueFaultCaught=$evidence.Contains('[handled:TaskQueueLoop]');JobFaultCaught=$evidence.Contains('[handled:JobReconcileLoop]');QueueRecovered=@($queue|Where-Object state -eq 'failed').Count;HostPid=$pidBefore;HostReplaced=$false;HandledErrors=$metrics.host.currentSession.handledErrors;CurrentHostCrashes=$metrics.host.currentSession.crashes;Workspace=$root}|Format-List
}
finally {
    if($hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_TEST_FAIL_BACKGROUND_LOOPS,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if((Test-Path -LiteralPath $root)-and$root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
}

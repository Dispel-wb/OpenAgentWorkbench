param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; $value = & $Action; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw ('Timed out waiting for Run Center state: ' + $script:WaitStage)
}
function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until { try { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $null } } { param($v) $null -ne $v -and $v.state -eq 'running' -and [int]$v.pid -ne $PreviousPid }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $args = @{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method}
    if ($null -ne $Body) {$args.ContentType='application/json; charset=utf-8';$args.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12 -Compress))}
    Invoke-RestMethod @args
}
function RunJson($Connection) {(Invoke-WebRequest -UseBasicParsing -Uri ($Connection.Base+'/api/chat/runs') -Headers $Connection.Headers).Content}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-run-center-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$vsRoot='C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc=Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json=Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if($LASTEXITCODE -ne 0){throw 'Fake Claude build failed'}
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')
Add-Type -LiteralPath $json
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='run-center-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
try {
    $connection=Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{id='offline-runs';name='Offline Runs';token='sk-secret-must-never-leak';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}} | Out-Null
    $sessionA=[guid]::NewGuid().ToString();$sessionB=[guid]::NewGuid().ToString()
    $common=@{workspace=$root;resume=$false;providerId='offline-runs';model='offline-model';effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
    $bodyA=$common.Clone();$bodyA.prompt='run-center-slow-a';$bodyA.sessionId=$sessionA;$bodyA.claudeSessionId=$sessionA
    $bodyB=$common.Clone();$bodyB.prompt='run-center-slow-b';$bodyB.sessionId=$sessionB;$bodyB.claudeSessionId=$sessionB
    $runA=Api $connection '/api/chat/start' 'POST' $bodyA
    $runB=Api $connection '/api/chat/start' 'POST' $bodyB
    $expires=(Get-Date).AddSeconds(20);do{$runsJson=RunJson $connection;$runs=[Newtonsoft.Json.Linq.JArray]::Parse($runsJson);if($runs.Count -eq 2){break};Start-Sleep -Milliseconds 100}while((Get-Date)-lt $expires)
    if($runs.Count -ne 2){throw 'Timed out waiting for two concurrent Runs'}
    $foundA=$false;$foundB=$false;$valid=0;foreach($run in $runs){$sid=[string]$run['sessionId'];if($sid-eq$sessionA){$foundA=$true};if($sid-eq$sessionB){$foundB=$true};if([long]$run['eventCount']-ge 0 -and [string]$run['startedAt'] -and [string]$run['model']-eq'offline-model'){$valid++}}
    if(-not $foundA -or -not $foundB){throw ('Run Center did not preserve per-session identity: '+$runsJson)}
    if($runsJson.Contains('sk-secret-must-never-leak')){throw 'Run Center leaked an API key'}
    if($valid-ne 2){throw 'Run Center summary is incomplete'}

    $pauseResult=Api $connection "/api/chat/pause/$($runB.jobId)" 'POST'
    $pausedRuns=[Newtonsoft.Json.Linq.JArray]::Parse((RunJson $connection));$pauseState='';foreach($run in $pausedRuns){if([string]$run['id']-eq[string]$runB.jobId){$pauseState=[string]$run['state'];break}}
    if(-not $pauseResult.paused -or $pauseState-ne'paused'){throw ('Pause state mismatch: response='+($pauseResult|ConvertTo-Json -Compress)+'; all='+$pausedRuns.ToString())}
    $pausedHostPid=[int]$connection.Runtime.pid
    Stop-Process -Id $pausedHostPid -Force
    Start-Sleep -Milliseconds 500
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection=Connect-Host $runtimePath $pausedHostPid
    $recoveredRuns=[Newtonsoft.Json.Linq.JArray]::Parse((RunJson $connection));$pauseState='';foreach($run in $recoveredRuns){if([string]$run['id']-eq[string]$runB.jobId){$pauseState=[string]$run['state'];break}}
    if($recoveredRuns.Count-ne 2-or$pauseState-ne'paused'){throw ('Paused Run did not survive Host restart: '+$recoveredRuns.ToString())}
    $pausedPoll=Api $connection "/api/chat/poll/$($runB.jobId)?after=0";if($pausedPoll.status.state-ne'paused'){throw 'Poll did not expose recovered paused state'}
    Api $connection "/api/chat/resume/$($runB.jobId)" 'POST' | Out-Null
    $resumedRuns=[Newtonsoft.Json.Linq.JArray]::Parse((RunJson $connection));$resumeState='';foreach($run in $resumedRuns){if([string]$run['id']-eq[string]$runB.jobId){$resumeState=[string]$run['state'];break}};if($resumeState-ne'running'){throw 'Resume state mismatch'}
    Api $connection "/api/chat/stop/$($runB.jobId)" 'POST' | Out-Null
    $script:WaitStage='cancel';$cancelled=Wait-Until { Api $connection "/api/chat/poll/$($runB.jobId)?after=0" } { param($v) $v.status.state -eq 'cancelled' }
    $script:WaitStage='complete';$completed=Wait-Until { Api $connection "/api/chat/poll/$($runA.jobId)?after=0" } { param($v) $v.status.state -eq 'completed' }
    if(([Newtonsoft.Json.Linq.JArray]::Parse((RunJson $connection))).Count -ne 0){throw 'Terminal Runs remained active in Run Center'}

    $terminalHostPid=[int]$connection.Runtime.pid
    Stop-Process -Id $terminalHostPid -Force
    Start-Sleep -Milliseconds 500
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection=Connect-Host $runtimePath $terminalHostPid
    $lazy=Api $connection "/api/chat/poll/$($runA.jobId)?after=0"
    if($lazy.status.state -ne 'completed' -or @($lazy.events).Count -eq 0){throw 'Terminal Run was not lazily recovered after Host restart'}
    [pscustomobject]@{ConcurrentRuns=2;Pause='OK';PausedHostRecovery='OK';Resume='OK';Stop='OK';TerminalRecovery='OK';SecretRedaction='OK';InitialHostPid=$pausedHostPid;RecoveredHostPid=$terminalHostPid;FinalHostPid=$connection.Runtime.pid;Workspace=$root}|Format-List
}
finally {
    if($connection -and $connection.Runtime.pid){Stop-Process -Id ([int]$connection.Runtime.pid) -Force -ErrorAction SilentlyContinue}
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $fake } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

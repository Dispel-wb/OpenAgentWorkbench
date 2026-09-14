param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = & $Action } catch { $value = $null }
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for Tool Runtime integration state'
}

function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($v) $null -ne $v -and $v.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{ 'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain); 'X-Workbench-Protocol'='2' } }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $arguments = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json; charset=utf-8'
        $arguments.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 16 -Compress))
    }
    Invoke-RestMethod @arguments
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-tool-runtime-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1')
$csc = $testDependencies.Compiler
$json = $testDependencies.Json
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'tool-runtime-' + [guid]::NewGuid().ToString('N')
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_CLAUDE_EXE = $fake
$hostProcess = $null
try {
    $runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{
        id='offline-tool-runtime';name='Offline Tool Runtime';token='stub-token';authStyle='bearer'
        text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | Out-Null

    $timeoutSession = [guid]::NewGuid().ToString()
    $timeout = Api $connection '/api/chat/start' 'POST' @{
        workspace=$root;prompt='tool-runtime-timeout';sessionId=$timeoutSession;claudeSessionId=$timeoutSession;resume=$false
        requestId=[guid]::NewGuid().ToString();providerId='offline-tool-runtime';model='offline-model';effort='low';permissionMode='readonly'
        attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()
        toolRuntimePolicy=@{maxDurationSeconds=1;maxInputBytes=4096;maxOutputBytes=16384;maxErrorBytes=4096}
    }
    $timeoutPoll = Wait-Until { Api $connection ("/api/chat/poll/$($timeout.jobId)?after=0") } { param($v) $null -ne $v -and $v.status.state -eq 'failed' } 15
    $timeoutEvidence = Api $connection "/api/chat/evidence/$($timeout.jobId)"
    if (@($timeoutEvidence.tools).Count -ne 1 -or $timeoutEvidence.tools[0].state -ne 'timed_out') { throw 'Timed-out ToolCall did not reach timed_out state' }
    if ([int]$timeoutEvidence.summary.timedOutTools -ne 1) { throw 'Timed-out ToolCall summary is incorrect' }
    $timeoutRequest = Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($timeout.jobId)\request.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$timeoutRequest.toolRuntimePolicy.maxDurationSeconds -ne 1 -or $timeoutRequest.toolRuntimePolicy.timeoutAction -ne 'cancel_run') { throw 'Tool Runtime policy was not persisted into request.json' }
    $timeoutStream = Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($timeout.jobId)\stream.jsonl") -Raw -Encoding UTF8
    if ($timeoutStream.Contains('sk-fixture-tool-runtime-secret')) { throw 'Tool secret leaked into stream.jsonl' }

    $cancelSession = [guid]::NewGuid().ToString()
    $cancel = Api $connection '/api/chat/start' 'POST' @{
        workspace=$root;prompt='tool-runtime-cancel';sessionId=$cancelSession;claudeSessionId=$cancelSession;resume=$false
        requestId=[guid]::NewGuid().ToString();providerId='offline-tool-runtime';model='offline-model';effort='low';permissionMode='readonly'
        attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()
        toolRuntimePolicy=@{maxDurationSeconds=30}
    }
    Wait-Until { Api $connection "/api/chat/evidence/$($cancel.jobId)" } { param($v) $null -ne $v -and @($v.tools).Count -eq 1 -and $v.tools[0].state -eq 'running' } | Out-Null
    Api $connection "/api/chat/stop/$($cancel.jobId)" 'POST' @{} | Out-Null
    $cancelEvidence = Wait-Until { Api $connection "/api/chat/evidence/$($cancel.jobId)" } { param($v) $null -ne $v -and @($v.tools).Count -eq 1 -and $v.tools[0].state -eq 'cancelled' }
    if ([int]$cancelEvidence.summary.cancelledTools -ne 1 -or [int]$cancelEvidence.summary.timedOutTools -ne 0) { throw 'Cancelled ToolCall evidence is incorrect' }

    [pscustomobject]@{
        ToolTimeout='OK';TimeoutTerminatesRun='OK';UserCancellation='OK';SecretRedaction='OK';PolicyPersistence='OK'
        TimeoutRun=$timeout.jobId;CancelledRun=$cancel.jobId;Workspace=$root
    } | Format-List
}
finally {
    if ($null -ne $hostProcess) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $fake } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE -ErrorAction SilentlyContinue
    Remove-Item Env:CLAUDE_GUI_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:CLAUDE_GUI_CLAUDE_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

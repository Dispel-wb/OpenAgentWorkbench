param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$DependencyRoot,
    [switch]$BridgeOnly
)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('agent-worker-sdk-中文 路径-' + [guid]::NewGuid().ToString('N'))
$fake = Join-Path $root 'fake-codex.exe'
$config = Join-Path $root 'bridge.json'
$state = Join-Path $root 'bridge-state.json'
[IO.Directory]::CreateDirectory($root) | Out-Null
$hostProcess = $null
$hostBase = ''
$jobId = ''
try {
    $csc = Join-Path ([IO.Path]::GetFullPath($DependencyRoot)) 'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe'
    & $csc /nologo /target:exe /platform:x64 "/out:$fake" (Join-Path $PSScriptRoot 'fake-codex-worker.cs')
    if ($LASTEXITCODE -ne 0) { throw "Fake Codex build failed: $LASTEXITCODE" }
    $payload = [ordered]@{
        schemaVersion = 1; harness = 'codex'; protocol = 'claude-stream-json-v1'; executable = $fake
        workspace = $root; model = 'fixture-model'; permissionMode = 'readonly'; sessionId = [guid]::NewGuid().ToString()
        addDirs = @(); statePath = $state
    }
    [IO.File]::WriteAllText($config, ($payload | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $first = '{"type":"user","message":{"role":"user","content":[{"type":"text","text":"第一轮中文"}]}}'
    $second = '{"type":"user","message":{"role":"user","content":[{"type":"text","text":"第二轮中文"}]}}'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [IO.Path]::GetFullPath($Executable)
    $start.Arguments = '--agent-worker-bridge "' + $config.Replace('"','\"') + '"'
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['WORKBENCH_TEST_PROCESS']='1'
    $process = [Diagnostics.Process]::Start($start)
    $inputBytes = [Text.Encoding]::UTF8.GetBytes($first + "`n" + $second + "`n")
    $process.StandardInput.BaseStream.Write($inputBytes,0,$inputBytes.Length); $process.StandardInput.BaseStream.Flush(); $process.StandardInput.Close()
    $outputTask = $process.StandardOutput.ReadToEndAsync(); $bridgeErrorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Agent Worker bridge timed out.' }
    $output = $outputTask.Result; $bridgeError = $bridgeErrorTask.Result
    if ($process.ExitCode -ne 0) { throw "Agent Worker bridge failed: $($process.ExitCode) $bridgeError $output" }
    $lines = $output -split "`r?`n" | Where-Object { $_ }
    $events = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
    $results = @($events | Where-Object { $_.type -eq 'result' })
    $messages = @($events | Where-Object { $_.type -eq 'assistant' })
    if ($results.Count -ne 2 -or @($results | Where-Object is_error).Count -ne 0) { throw ('Bridge did not produce two successful terminal results. Events: ' + ($events | ConvertTo-Json -Depth 8 -Compress)) }
    if ($messages.Count -ne 2 -or @($messages | Where-Object { $_.message.content[0].text -notmatch '中文' }).Count -ne 0) { throw 'Bridge did not normalize UTF-8 assistant messages.' }
    $saved = Get-Content -LiteralPath $state -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($saved.threadId -ne '11111111-1111-4111-8111-111111111111') { throw 'Codex thread id was not persisted for resume.' }
    foreach ($failureCase in @('fixture-terminal-failure','fixture-no-terminal','fixture-hang-after-terminal','fixture-malformed','fixture-oversize','fixture-answer-limit')) {
        $failureProcess = [Diagnostics.Process]::Start($start)
        try {
            $failureOutput = $failureProcess.StandardOutput.ReadToEndAsync()
            $failureError = $failureProcess.StandardError.ReadToEndAsync()
            $failureInput = [Text.Encoding]::UTF8.GetBytes(('{"type":"user","message":{"content":[{"type":"text","text":"中文 '+$failureCase+'"}]}}')+"`n")
            $failureProcess.StandardInput.BaseStream.Write($failureInput,0,$failureInput.Length); $failureProcess.StandardInput.Close()
            if (-not $failureProcess.WaitForExit(15000)) { throw 'Failure fixture timed out.' }
            $failureResults = @($failureOutput.Result -split "`r?`n" | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object type -eq result)
            $expectError=$failureCase -ne 'fixture-hang-after-terminal'
            if ($failureResults.Count -ne 1 -or [bool]$failureResults[0].is_error -ne $expectError) { throw "Codex terminal classification failed: $failureCase" }
            $expectedReason=@{'fixture-malformed'='无效 JSON';'fixture-oversize'='character limit';'fixture-answer-limit'='安全长度上限'}[$failureCase]
            if($expectedReason -and $failureResults[0].result -notmatch $expectedReason){throw "Wrong Codex failure reason: $failureCase"}
            if(-not $expectError -and $failureOutput.Result -notmatch 'worker_cleanup'){throw 'Post-terminal hung process was not cleaned up'}
        } finally { if (-not $failureProcess.HasExited) { $failureProcess.Kill() }; $failureProcess.Dispose() }
    }
    if($BridgeOnly){[pscustomobject]@{AgentWorkerBridge='PASS';Turns=2;FaultCases=6;HostIntegration='NOT_RUN'}|Format-List;return}
    $hostStart = [Diagnostics.ProcessStartInfo]::new()
    $hostStart.FileName = [IO.Path]::GetFullPath($Executable)
    $hostStart.Arguments = '--host'; $hostStart.UseShellExecute = $false; $hostStart.CreateNoWindow = $true
    $hostStart.EnvironmentVariables['CLAUDE_GUI_WORKSPACE'] = $root
    $hostStart.EnvironmentVariables['CLAUDE_GUI_ROOT'] = Join-Path $root '安装 路径'
    $hostStart.EnvironmentVariables['CLAUDE_GUI_TEST_MODE'] = '1'
    $hostStart.EnvironmentVariables['CLAUDE_GUI_MUTEX_SCOPE'] = 'codex-providerless-' + [guid]::NewGuid().ToString('N')
    $hostStart.EnvironmentVariables['CLAUDE_GUI_CODEX_EXE'] = $fake
    $hostProcess = [Diagnostics.Process]::Start($hostStart)
    $runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $deadline)
    if ($null -eq $runtime -or $runtime.state -ne 'running') {
        $startupLogs=@('native-crash.log','native-runtime.log')|ForEach-Object{Get-Content -LiteralPath (Join-Path $root ('.claude-gui-v2/'+$_)) -Tail 12 -ErrorAction SilentlyContinue}
        throw ('Providerless Host did not start. Exited='+$hostProcess.HasExited+' '+($startupLogs -join "`n"))
    }
    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers = @{'X-Desktop-Secret'=$secret; 'X-Workbench-Protocol'='2'}
    $hostBase = "http://127.0.0.1:$($runtime.port)"
    foreach ($core in @('codex','dsh','pi')) {
        foreach ($mode in @('manual','scoped',' Manual ')) {
            $unsafeBody = [Text.Encoding]::UTF8.GetBytes((@{workerHarness=$core;permissionMode=$mode;prompt='fixture denied before launch'}|ConvertTo-Json -Compress))
            $rejected = $false
            try { Invoke-RestMethod "$hostBase/api/chat/start" -Method Post -Headers $headers -Body $unsafeBody -ContentType 'application/json' -TimeoutSec 5 | Out-Null }
            catch { $rejected = $_.ErrorDetails.Message -match 'worker_capability_unsupported' }
            if (-not $rejected) { throw "$core silently accepted unsupported $mode approvals" }
        }
    }
    foreach ($active in @($true,$false)) {
        $memory = @{workspace=$root;id=[guid]::NewGuid().ToString();title='Core fixture';content=$(if($active){'fixture-core-memory'}else{'fixture-inactive-memory'});active=$active}
        Invoke-RestMethod "$hostBase/api/workbench/memories" -Headers $headers -Method Post -Body ([Text.Encoding]::UTF8.GetBytes(($memory|ConvertTo-Json -Compress))) -ContentType 'application/json'|Out-Null
    }
    $request = @{workspace=$root; prompt='验证中文无 API 工作链路 fixture-require-memory'; workerHarness='codex'; workerModel='fixture-model'; providerId=''; model=''; permissionMode='readonly'; sessionId=[guid]::NewGuid().ToString()}
    $body = [Text.Encoding]::UTF8.GetBytes(($request | ConvertTo-Json -Compress))
    $preview = Invoke-RestMethod "$hostBase/api/chat/context-preview" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 10
    if ($preview.decision -eq 'invalid' -or $preview.decision -eq 'blocked') { throw 'Providerless context preview failed.' }
    $chat = Invoke-RestMethod "$hostBase/api/chat/start" -Method Post -Headers $headers -Body $body -ContentType 'application/json' -TimeoutSec 15
    $jobId = $chat.jobId
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 100
        $poll = Invoke-RestMethod "$hostBase/api/chat/poll/$jobId" -Headers $headers -TimeoutSec 5
        $terminal = @($poll.lines | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.type -eq 'result' })
    } while ($terminal.Count -eq 0 -and (Get-Date) -lt $deadline)
    if ($terminal.Count -eq 0 -or $terminal[-1].is_error) { throw ('Providerless Codex run failed: ' + ($poll | ConvertTo-Json -Depth 8 -Compress)) }
    $stored=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$jobId\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if($stored.permissionBrokerEnabled -or $stored.claudeCodeIsolation -or $stored.coreIsolation.core -ne 'codex'){throw 'Codex run falsely advertised Claude policy enforcement'}
    $hostProcess.Kill();$hostProcess.WaitForExit(5000)|Out-Null
    $lostRoots=@()
    foreach($lostCore in @('dsh','pi')){
        $lostRunId=[guid]::NewGuid().ToString();$lostRoot=Join-Path $root ".claude-gui-v2\runs\$lostRunId";$lostRoots+=$lostRoot
        [IO.Directory]::CreateDirectory($lostRoot)|Out-Null
        $stored.workerHarness=$lostCore;$stored.guiSessionId=$lostRunId;$stored.sessionId=$lostRunId
        [IO.File]::WriteAllText((Join-Path $lostRoot 'request.json'),($stored|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $lostRoot 'status.json'),'{"state":"running"}',[Text.UTF8Encoding]::new($false))
    }
    $hostProcess=[Diagnostics.Process]::Start($hostStart)
    foreach($lostRoot in $lostRoots){
    $deadline=(Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $lostStatus=Get-Content -LiteralPath (Join-Path $lostRoot 'status.json') -Raw -Encoding UTF8|ConvertFrom-Json
    } while($lostStatus.state -eq 'running' -and (Get-Date) -lt $deadline)
    if($lostStatus.state -ne 'failed' -or $lostStatus.message -notmatch '未自动重放' -or (Test-Path (Join-Path $lostRoot 'pid.txt'))){throw 'Lost DSH/Pi core was automatically replayed'}
    }
    [pscustomobject]@{ AgentWorkerSdk = 'PASS'; Protocol = 'claude-stream-json-v1'; CodexTurns = $results.Count; Utf8 = $true; ResumeState = $true; ProviderlessHostRun = $true; TerminalFailurePreserved = $true; UnsupportedApprovalDenied = $true; ActiveMemoryDelivered = $true; LostDshReplaySuppressed = $true; LostPiReplaySuppressed = $true } | Format-List
} finally {
    if ($jobId -and $hostBase) { try { Invoke-RestMethod "$hostBase/api/chat/stop/$jobId" -Method Post -Headers $headers -TimeoutSec 5 | Out-Null } catch {} }
    if ($hostProcess -and -not $hostProcess.HasExited) { $hostProcess.Kill(); $hostProcess.WaitForExit(5000) | Out-Null }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolved = [IO.Path]::GetFullPath($root)
    if ((Test-Path -LiteralPath $resolved) -and $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

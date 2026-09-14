param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for child Agent integration state'
}
function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($v) $null -ne $v -and $v.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    @{Runtime=$runtime;Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}}
}
function Api($Connection,[string]$Path,[string]$Method='GET',$Body=$null) {
    $arguments=@{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method}
    if($null-ne$Body){$arguments.ContentType='application/json; charset=utf-8';$arguments.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 20 -Compress))}
    Invoke-RestMethod @arguments
}
function Api-Failure($Connection,[string]$Path,$Body) {
    try { Api $Connection $Path 'POST' $Body | Out-Null; throw 'Expected API failure' }
    catch { if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }; throw }
}

$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-child-agent-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $root 'fake-claude.exe';$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1');$csc = $testDependencies.Compiler;$json = $testDependencies.Json
&$csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs');if($LASTEXITCODE-ne 0){throw 'Fake Claude build failed'};Copy-Item $json (Join-Path $root 'Newtonsoft.Json.dll')
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='child-agent-'+[guid]::NewGuid().ToString('N')
$hostProcess=$null
try {
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection=Connect-Host (Join-Path $root '.claude-gui-v2\runtime-state.json')
    Api $connection '/api/providers' 'POST' @{id='offline-child';name='Offline Child';token='stub-token';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    $parentSession=[guid]::NewGuid().ToString()
    $parent=Api $connection '/api/chat/start' 'POST' @{workspace=$root;prompt='parent-agent-root';sessionId=$parentSession;claudeSessionId=$parentSession;resume=$false;requestId=[guid]::NewGuid().ToString();providerId='offline-child';model='offline-model';effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
    Wait-Until {Api $connection "/api/chat/poll/$($parent.jobId)?after=0"} {param($v)$null-ne$v-and$v.status.state-eq'completed'}|Out-Null

    $child=Api $connection '/api/agents/children/start' 'POST' @{parentRunId=$parent.jobId;name='code-review';prompt='child-agent-result'}
    Wait-Until {Api $connection "/api/chat/poll/$($child.jobId)?after=0"} {param($v)$null-ne$v-and$v.status.state-eq'completed'}|Out-Null
    $children=Wait-Until {Api $connection "/api/agents/children?parentRunId=$($parent.jobId)"} {param($v)@($v).Count-eq 1-and$v[0].state-eq'completed'}
    if($children[0].handoffState-ne'ready'-or$children[0].metadata.inheritance-ne'restrict_only'){throw 'Completed child Agent is missing its ready handoff'}
    $childRequest=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($child.jobId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if($childRequest.parentRunId-ne$parent.jobId-or$childRequest.agentRuntime.depth-ne 1-or$childRequest.provider.id-ne'offline-child'-or$childRequest.permissionMode-ne'readonly'){throw 'Child Agent did not inherit the parent boundary'}

    $handoff=Api $connection "/api/agents/children/handoff/$($child.jobId)" 'POST' @{parentRunId=$parent.jobId}
    if($handoff.handoffState-ne'accepted'-or-not$handoff.injectedIntoParent){throw 'Completed child result was not handed off to the live parent Worker'}
    $parentEvidence=Wait-Until {Api $connection "/api/chat/evidence/$($parent.jobId)"} {param($v)$null-ne$v-and@($v.context.sources|Where-Object {$_.type -eq 'child-agent-handoff'}).Count-eq 1}
    if($parentEvidence.summary.pendingHandoffs-ne 0){throw 'Accepted child handoff remained pending'}

    $tamperedSession=[guid]::NewGuid().ToString()
    $tampered=Api $connection '/api/chat/start' 'POST' @{parentRunId=$parent.jobId;workspace='C:\';prompt='tampered-child';sessionId=$tamperedSession;claudeSessionId=$tamperedSession;resume=$false;requestId=[guid]::NewGuid().ToString();providerId='does-not-exist';model='elevated-model';maxTurns=500;permissionMode='full';toolRuntimePolicy=@{maxDurationSeconds=86400}}
    Wait-Until {Api $connection "/api/chat/poll/$($tampered.jobId)?after=0"} {param($v)$null-ne$v-and$v.status.state-eq'completed'}|Out-Null
    $tamperedRequest=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($tampered.jobId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if($tamperedRequest.sourceWorkspace-ne$root-or$tamperedRequest.provider.id-ne'offline-child'-or$tamperedRequest.model-ne'offline-model'-or$tamperedRequest.maxTurns-ne 100-or$tamperedRequest.permissionMode-ne'readonly'-or$tamperedRequest.toolRuntimePolicy.maxDurationSeconds-ne 1800){throw 'Tampered child request escaped parent inheritance'}

    $grand=Api $connection '/api/agents/children/start' 'POST' @{parentRunId=$child.jobId;name='grandchild-task';prompt='grandchild-agent-result'}
    Wait-Until {Api $connection "/api/chat/poll/$($grand.jobId)?after=0"} {param($v)$null-ne$v-and$v.status.state-eq'completed'}|Out-Null
    $grandRequest=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($grand.jobId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if($grandRequest.agentRuntime.depth-ne 2){throw 'Grandchild depth was not persisted'}
    $tooDeep=Api-Failure $connection '/api/agents/children/start' @{parentRunId=$grand.jobId;prompt='must-be-blocked'}
    if($tooDeep-ne 409){throw "Delegation depth was not blocked: $tooDeep"}

    $failed=Api $connection '/api/agents/children/start' 'POST' @{parentRunId=$parent.jobId;name='failing-child';prompt='provider-auth-failure';maxRetries=1}
    Wait-Until {Api $connection "/api/chat/poll/$($failed.jobId)?after=0"} {param($v)$null-ne$v-and$v.status.state-eq'failed'}|Out-Null
    $failedEvidence=Wait-Until {Api $connection "/api/chat/evidence/$($parent.jobId)"} {param($v)$null-ne$v-and[int]$v.summary.failedChildAgents-ge 1}
    $failedChild=@($failedEvidence.childAgents|Where-Object {$_.childRunId -eq $failed.jobId})[0]
    if($failedChild.handoffState-ne'ready'-or-not$failedChild.error){throw 'Failed child did not propagate a parent-visible warning'}
    $retry=Api $connection "/api/agents/children/retry/$($failed.jobId)" 'POST' @{parentRunId=$parent.jobId}
    if($retry.jobId -eq $failed.jobId){throw 'Retry unexpectedly reused the failed Run'}
    Wait-Until {Api $connection "/api/chat/poll/$($retry.jobId)?after=0"} {param($v)$null-ne$v-and$v.status.state-eq'failed'}|Out-Null
    $retryRequest=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($retry.jobId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if($retryRequest.attempt-ne 2-or$retryRequest.retryOf-ne$failed.jobId){throw 'Retry attempt chain was not persisted'}
    $retryLimit=Api-Failure $connection "/api/agents/children/retry/$($retry.jobId)" @{parentRunId=$parent.jobId}
    if($retryLimit-ne 409){throw "Retry limit was not enforced: $retryLimit"}

    [pscustomobject]@{IndependentChildRun='OK';RestrictOnlyInheritance='OK';TamperResistance='OK';ExplicitHandoff='OK';GrandchildDepth='OK';DepthLimit='OK';FailurePropagation='OK';RetryAttempt='OK';RetryLimit='OK';ParentRun=$parent.jobId;ChildRun=$child.jobId;Workspace=$root}|Format-List
}
finally {
    if($null-ne$hostProcess){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}
    Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath-eq$fake}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

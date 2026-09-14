param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; try { $value = & $Action } catch { $value = $null; $lastError=$_.Exception.Message }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw ("Timed out: "+$script:stage+" error="+$lastError+" state="+($value|ConvertTo-Json -Depth 8 -Compress))
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

function Find-Workflow($Connection,[string]$Id) { $list=Api $Connection '/api/workflows'; foreach($item in $list){if($item.id-eq$Id){return $item}} }
$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-child-agent-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $root 'fake-claude.exe';$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1');$csc = $testDependencies.Compiler;$json = $testDependencies.Json
&$csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs');if($LASTEXITCODE-ne 0){throw 'Fake Claude build failed'};Copy-Item $json (Join-Path $root 'Newtonsoft.Json.dll')
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='child-agent-'+[guid]::NewGuid().ToString('N')
$hostProcess=$null
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='dag-'+[guid]::NewGuid().ToString('N')
$hostProcess=$null
try {
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection=Connect-Host (Join-Path $root '.claude-gui-v2\runtime-state.json')
    Api $connection '/api/providers' 'POST' @{id='offline-dag';name='Offline';token='stub-token';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    $parents=@();$script:stage='parent completion'
    foreach($name in @('one','two')){
      $session=[guid]::NewGuid().ToString()
      $parent=Api $connection '/api/chat/start' 'POST' @{workspace=$root;prompt="parent-$name";sessionId=$session;claudeSessionId=$session;providerId='offline-dag';model='offline-model';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
      Wait-Until {Api $connection "/api/chat/poll/$($parent.jobId)?after=0"} {param($v)$v.status.state-eq'completed'}|Out-Null
      $parents+= $parent.jobId
    }
    $cycle=Api-Failure $connection '/api/workflows' @{nodes=@(@{id='a';parentRunId=$parents[0];prompt='a';dependencies=@('b')},@{id='b';parentRunId=$parents[1];prompt='b';dependencies=@('a')})}
    if($cycle-ne 400){throw 'Cycle accepted'}
    $created=Api $connection '/api/workflows' 'POST' @{name='cross-parent';maxParallel=2;nodes=@(@{id='a';parentRunId=$parents[0];prompt='alpha-result'},@{id='b';parentRunId=$parents[1];prompt='beta-result';dependencies=@('a');includeDependencyResults=$true})}
    $script:stage='DAG completion'
    $done=Wait-Until { Find-Workflow $connection $created.id } {param($v)$v.state-eq'completed'} 40
    foreach($node in $done.nodes){
      $request=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($node.runId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
      if($request.parentRunId-ne $node.parentRunId-or $request.permissionMode-ne'readonly'-or $request.provider.id-ne'offline-dag'){throw 'DAG inherited wrong parent'}
      if($node.id-eq'b'-and $request.prompt-notmatch 'alpha-result'){throw 'Explicit dependency result handoff missing'}
    }
    $paused=Api $connection '/api/workflows' 'POST' @{name='paused';nodes=@(@{id='first';parentRunId=$parents[0];prompt='hold'})}
    Api $connection '/api/workflows' 'POST' @{id=$paused.id;action='pause'}|Out-Null
    $snapshot=Find-Workflow $connection $paused.id
    if(-not $snapshot.paused){throw 'Pause not durable'}
    Api $connection '/api/workflows' 'POST' @{id=$paused.id;action='cancel'}|Out-Null
    $snapshot=Find-Workflow $connection $paused.id
    if($snapshot.state-ne'cancelled'){throw 'Cancel failed'}
    Stop-Process -Id $hostProcess.Id -Force
    $hostProcess.WaitForExit()
    $runtimeFile=Join-Path $root '.claude-gui-v2\runtime-state.json'
    # Remove only the isolated fixture's stale discovery file before restart.
    Remove-Item -LiteralPath $runtimeFile -Force
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection=Connect-Host $runtimeFile
    $restored=Find-Workflow $connection $created.id
    if($restored.state-ne'completed'-or $restored.nodes[1].runId-ne$done.nodes[1].runId){throw 'Restart lost DAG or replayed node'}
    # Inject a crash-after-intent-before-dispatch fixture; ambiguous work must not auto replay.
    $ambiguous=Api $connection '/api/workflows' 'POST' @{nodes=@(@{id='uncertain';parentRunId=$parents[0];prompt='must-not-replay'})}
    Api $connection '/api/workflows' 'POST' @{id=$ambiguous.id;action='pause'}|Out-Null
    $file=Join-Path $root ".claude-gui-v2\workflows\$($ambiguous.id).json"
    $state=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json
    $state.nodes[0].state='dispatching'
    [IO.File]::WriteAllText($file,($state|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
    $attention=Wait-Until { Find-Workflow $connection $ambiguous.id } {param($v)$v.nodes[0].state-eq'attention'}
    if($attention.nodes[0].runId){throw 'Ambiguous node was replayed'}
    Write-Output 'PASS DAG integration: two parents, dependency order, handoff, inherited permissions, cycle rejection, pause/cancel, restart, ambiguous dispatch containment.'
} finally {
    if($hostProcess-and -not $hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and [IO.Path]::GetFileName($resolved).StartsWith('claude-child-agent-')){Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue}
}

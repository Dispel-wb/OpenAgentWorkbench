param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    $lastError=$null;$value=$null
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action;$lastError=$null } catch { $value = $null;$lastError=$_.Exception.Message }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    $lastValue=if($null-ne$value){$value|ConvertTo-Json -Depth 8 -Compress}else{'null'}
    throw "Timed out waiting for workspace memory integration state (stage: $script:stage; lastError: $lastError; lastValue: $lastValue)"
}
function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until { try { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $null } } {
        param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -ne $PreviousPid
    }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method}
    if ($null -ne $Body) {$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress))}
    Invoke-RestMethod @request
}
function Rejected($Connection, [string]$Path, $Body) {
    $response=Invoke-WebRequest -UseBasicParsing -SkipHttpErrorCheck -Uri ($Connection.Base+$Path) -Headers $Connection.Headers -Method POST -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress)))
    @{Status=[int]$response.StatusCode;Body=($response.Content|ConvertFrom-Json)}
}
function Wait-Run($Connection,[string]$RunId) {
    Wait-Until { Api $Connection ("/api/chat/poll/${RunId}?after=0") } { param($value) ($value.status.terminalState ?? $value.status.state) -eq 'completed' }
}

$root=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-workspace-memory-'+[guid]::NewGuid().ToString('N'))));[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $root 'fake-claude.exe';$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1');$csc = $testDependencies.Compiler;$json = $testDependencies.Json
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs');if($LASTEXITCODE-ne0){throw 'Fake Claude build failed'};Copy-Item $json (Join-Path $root 'Newtonsoft.Json.dll')
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='workspace-memory-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json';$hostProcess=$null
try {
    $script:stage='connect-first-host'
    $hostProcess=Start-Process $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{id='offline-memory';name='Offline Memory';token='stub-token';authStyle='bearer';text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@('memory-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()};capabilities=@{schemaVersion=2;evidencePolicy='fixture';models=@{'memory-model'=@{contextWindow=32768;maxOutputTokens=4096;tools=$true;evidence='fixture'}}}}|Out-Null
    $empty=Api $connection ('/api/workbench/memories?workspace='+[Uri]::EscapeDataString($root));if(@($empty.items).Count-ne0){throw 'Fresh workspace unexpectedly contained memories'}
    $activeId=[guid]::NewGuid().ToString();$inactiveId=[guid]::NewGuid().ToString();$activeText='默认使用中文回答；技术专有名词保留英文。';$inactiveText='这条停用记忆绝不能进入 system prompt。'
    Api $connection '/api/workbench/memories' 'POST' @{workspace=$root;id=$activeId;title='语言偏好';content=$activeText;active=$true}|Out-Null
    Api $connection '/api/workbench/memories' 'POST' @{workspace=$root;id=$inactiveId;title='停用记忆';content=$inactiveText;active=$false}|Out-Null
    $request=@{workspace=$root;prompt='workspace-memory-fixture';attachments=@();allowedDirs=@();sessionId=[guid]::NewGuid().ToString();claudeSessionId=[guid]::NewGuid().ToString();resume=$false;providerId='offline-memory';model='memory-model';effort='low';permissionMode='readonly';allowedTools=@();disallowedTools=@()};$request.claudeSessionId=$request.sessionId
    $preview=Api $connection '/api/chat/context-preview' 'POST' $request
    if([int]$preview.workspaceMemories-ne1-or[long]$preview.workspaceMemoryTokens-le0-or-not@($preview.sources|Where-Object type -eq 'workspace-memory').Count){throw 'Context preview did not count active workspace memory'}
    $script:stage='first-run';$first=Api $connection '/api/chat/start' 'POST' (@{}+$request+@{requestId='memory-first-'+[guid]::NewGuid().ToString('N')});Wait-Run $connection $first.jobId|Out-Null
    $runRoot=Join-Path $root ('.claude-gui-v2\runs\'+$first.jobId);$stored=Get-Content (Join-Path $runRoot 'request.json') -Raw -Encoding UTF8|ConvertFrom-Json;$systemPath=[string]$stored.trustedInstructionPath;$system=Get-Content $systemPath -Raw -Encoding UTF8
    if(-not$system.Contains($activeText)-or$system.Contains($inactiveText)-or@($stored.workspaceMemories).Count-ne1-or[string]$stored.workspaceMemories[0].id-ne$activeId){throw 'Worker system prompt did not contain the exact active-memory snapshot'}
    $evidence=Api $connection ('/api/chat/evidence/'+$first.jobId);$memoryEvidence=@($evidence.context.sources|Where-Object type -eq 'workspace-memory');if($memoryEvidence.Count-ne1-or[string]$memoryEvidence[0].label-ne'语言偏好'-or-not[string]$memoryEvidence[0].sha256){throw 'Workspace memory evidence was not persisted'}
    if(($evidence|ConvertTo-Json -Depth 20 -Compress).Contains($activeText)){throw 'Full memory content leaked into Run evidence instead of hash-only proof'}
    $script:stage='second-run-start';$secondRequest=@{}+$request;$secondRequest.resume=$true;$secondRequest.requestId='memory-reuse-'+[guid]::NewGuid().ToString('N');$second=Api $connection '/api/chat/start' 'POST' $secondRequest;$script:stage='second-run-wait';Wait-Run $connection $second.jobId|Out-Null;if($second.jobId-ne$first.jobId-or-not$second.reused){throw 'Unchanged memory snapshot did not preserve safe Worker reuse'}
    Api $connection '/api/workbench/memories/state' 'POST' @{workspace=$root;id=$activeId;active=$false}|Out-Null
    $without=Api $connection '/api/chat/context-preview' 'POST' $request;if([int]$without.workspaceMemories-ne0-or[long]$without.workspaceMemoryTokens-ne0){throw 'Disabled memory remained in Context preview'}
    $script:stage='third-run-start';$thirdRequest=@{}+$request;$thirdRequest.resume=$true;$thirdRequest.requestId='memory-changed-'+[guid]::NewGuid().ToString('N');$third=Api $connection '/api/chat/start' 'POST' $thirdRequest;$script:stage='third-run-wait';Wait-Run $connection $third.jobId|Out-Null;if($third.jobId-eq$first.jobId){throw 'Changed memory snapshot reused a Worker with stale system instructions'}
    $tooLong=Rejected $connection '/api/workbench/memories' @{workspace=$root;title='too-long';content=('x'*8001);active=$false};if($tooLong.Status-ne400){throw 'Oversized memory item was not rejected'}
    $countWorkspace=Join-Path $root 'memory-count-limit';[IO.Directory]::CreateDirectory($countWorkspace)|Out-Null
    1..32|ForEach-Object{Api $connection '/api/workbench/memories' 'POST' @{workspace=$countWorkspace;title=('count-'+$_);content='x';active=$true}|Out-Null}
    $tooMany=Rejected $connection '/api/workbench/memories' @{workspace=$countWorkspace;title='count-33';content='x';active=$true};if($tooMany.Status-ne400){throw 'Thirty-third active workspace memory was not rejected'}
    $characterWorkspace=Join-Path $root 'memory-character-limit';[IO.Directory]::CreateDirectory($characterWorkspace)|Out-Null
    1..3|ForEach-Object{Api $connection '/api/workbench/memories' 'POST' @{workspace=$characterWorkspace;title=('characters-'+$_);content=('x'*8000);active=$true}|Out-Null}
    $tooManyCharacters=Rejected $connection '/api/workbench/memories' @{workspace=$characterWorkspace;title='characters-overflow';content='x';active=$true};if($tooManyCharacters.Status-ne400){throw 'Workspace active-memory character budget was not enforced'}
    Api $connection '/api/workbench/memories/delete' 'POST' @{workspace=$root;id=$activeId}|Out-Null
    $oldEvidence=Api $connection ('/api/chat/evidence/'+$first.jobId);if(@($oldEvidence.context.sources|Where-Object type -eq 'workspace-memory').Count-lt1){throw 'Deleting memory rewrote historical Run evidence'}
    $script:stage='restart-host';$oldPid=[int]$connection.Runtime.pid;Stop-Process -Id $oldPid -Force;$hostProcess=Start-Process $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath $oldPid
    $persisted=Api $connection ('/api/workbench/memories?workspace='+[Uri]::EscapeDataString($root));if(@($persisted.items).Count-ne1-or[string]$persisted.items[0].id-ne$inactiveId){throw 'Workspace memory state did not persist across Host restart'}
    [pscustomobject]@{WorkspaceMemoryIntegration='PASS';SchemaV11=$true;ActiveOnlyInjected=$true;ContextBudgetEvidence=$true;HashOnlyRunEvidence=$true;SafeWorkerReuse=$true;ChangedSnapshotRetiredWorker=$true;DisabledExcluded=$true;ItemCharacterLimit=$true;ActiveItemLimit=$true;ActiveCharacterLimit=$true;DeletionPreservedHistory=$true;RestartPersistence=$true;Workspace=$root}|Format-List
}
finally {
    if($hostProcess){$candidate=Get-CimInstance Win32_Process -Filter "ProcessId=$($hostProcess.Id)" -ErrorAction SilentlyContinue;if($null-ne$candidate-and[string]::Equals([string]$candidate.ExecutablePath,$Executable,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}}
    Get-CimInstance Win32_Process|Where-Object{[string]::Equals([string]$_.ExecutablePath,$fake,[StringComparison]::OrdinalIgnoreCase)}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

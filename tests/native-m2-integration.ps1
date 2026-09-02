param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw ('Timed out waiting for M2 integration state: '+[string]$script:stage)
}
function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($v) $null -ne $v -and $v.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Secret=[Text.Encoding]::UTF8.GetString($plain); Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection,[string]$Path,[string]$Method='GET',$Body=$null) {
    $request=@{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method}
    if($null -ne $Body){$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12 -Compress))}
    Invoke-RestMethod @request
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-m2-'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $root 'fake-claude.exe';$vsRoot='C:\Program Files\Microsoft Visual Studio\2022\Community';$csc=Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe';$json=Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs');if($LASTEXITCODE -ne 0){throw 'Fake worker build failed'};Copy-Item $json (Join-Path $root 'Newtonsoft.Json.dll')
$workspace=Join-Path $root 'repo';[IO.Directory]::CreateDirectory($workspace)|Out-Null
& git -C $workspace init | Out-Null;& git -C $workspace config user.email workbench@example.invalid;& git -C $workspace config user.name Workbench-Test
[IO.File]::WriteAllText((Join-Path $workspace 'base.txt'),'base',[Text.UTF8Encoding]::new($false));& git -C $workspace add base.txt;& git -C $workspace commit -m initial | Out-Null
$env:CLAUDE_GUI_WORKSPACE=$workspace;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='m2-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $workspace '.claude-gui-v2\runtime-state.json';$hostProcess=$null;$worktree='';$sourceTranscript='';$workerTranscript=''
try {
    $script:stage='Host startup'
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $workspace -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{id='offline-m2';name='Offline M2';token='stub';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    $session=[guid]::NewGuid().ToString()
    $sourceKey=-join ([IO.Path]::GetFullPath($workspace).ToCharArray()|ForEach-Object{if([char]::IsLetterOrDigit($_)-or $_ -eq '-'-or $_ -eq '_'){$_}else{'-'}})
    $sourceTranscriptDirectory=Join-Path ([Environment]::GetFolderPath('UserProfile')) ('.claude\projects\'+$sourceKey)
    [IO.Directory]::CreateDirectory($sourceTranscriptDirectory)|Out-Null
    $sourceTranscript=Join-Path $sourceTranscriptDirectory ($session+'.jsonl')
    $sourceEntry=@{type='user';cwd=$workspace;timestamp=[DateTime]::UtcNow.ToString('o');message=@{content=('read '+(Join-Path $workspace 'base.txt'))}}|ConvertTo-Json -Depth 8 -Compress
    $sourceUsage=@{type='assistant';cwd=$workspace;timestamp=[DateTime]::UtcNow.ToString('o');message=@{id=[guid]::NewGuid().ToString();model='offline';content=@();usage=@{input_tokens=800;output_tokens=100;cache_read_input_tokens=2000;cache_creation_input_tokens=0}}}|ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText($sourceTranscript,$sourceEntry+[Environment]::NewLine+$sourceUsage+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    $job=Api $connection '/api/chat/start' 'POST' @{workspace=$workspace;prompt='m2-write';sessionId=$session;claudeSessionId=$session;resume=$true;requestId='m2-request';providerId='offline-m2';model='offline';effort='low';permissionMode='agent';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
    $script:stage='Agent completion'
    $poll=Wait-Until {Api $connection ("/api/chat/poll/$($job.jobId)?after=0")} {param($v)$null -ne $v -and $v.status.state -eq 'completed'}
    $isolation=Api $connection ("/api/workbench/task/isolation?jobId=$($job.jobId)");$worktree=[string]$isolation.worktreeRoot
    if($isolation.kind -ne 'git-worktree'){throw 'Agent did not use a Git worktree'}
    $requestState=Get-Content -LiteralPath (Join-Path $workspace ".claude-gui-v2\runs\$($job.jobId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if(-not $requestState.resume){throw 'Resume was disabled after switching to the isolated worktree'}
    $workerKey=-join ([IO.Path]::GetFullPath([string]$isolation.workerWorkspace).ToCharArray()|ForEach-Object{if([char]::IsLetterOrDigit($_)-or $_ -eq '-'-or $_ -eq '_'){$_}else{'-'}})
    $workerTranscript=Join-Path ([Environment]::GetFolderPath('UserProfile')) ('.claude\projects\'+$workerKey+'\'+$session+'.jsonl')
    if(-not (Test-Path -LiteralPath $workerTranscript)){throw 'Source transcript was not mirrored for the isolated worker'}
    if(-not (Test-Path -LiteralPath ($workerTranscript+'.workbench.json'))){throw 'Transcript mirror descriptor is missing'}
    $mirrored=Get-Content -LiteralPath $workerTranscript -Encoding UTF8|Select-Object -First 1|ConvertFrom-Json
    if([IO.Path]::GetFullPath([string]$mirrored.workbenchSourceCwd) -ne [IO.Path]::GetFullPath($workspace)){throw 'Mirrored transcript lost its source-workspace identity'}
    if([IO.Path]::GetFullPath([string]$mirrored.cwd) -ne [IO.Path]::GetFullPath([string]$isolation.workerWorkspace)){throw 'Mirrored transcript did not rewrite cwd to the worker workspace'}
    if(([string]$mirrored.message.content).IndexOf([string]$isolation.workerWorkspace,[StringComparison]::OrdinalIgnoreCase)-lt 0){throw 'Mirrored transcript did not rewrite workspace path references'}
    $latestUsage=@{type='assistant';cwd=[string]$isolation.workerWorkspace;timestamp=[DateTime]::UtcNow.AddSeconds(2).ToString('o');message=@{id=[guid]::NewGuid().ToString();model='offline';content=@();usage=@{input_tokens=3000;output_tokens=900;cache_read_input_tokens=6000;cache_creation_input_tokens=0}}}|ConvertTo-Json -Depth 10 -Compress
    [IO.File]::AppendAllText($workerTranscript,$latestUsage+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    Start-Sleep -Milliseconds 2200
    $historyPreview=Api $connection '/api/chat/context-preview' 'POST' @{workspace=$workspace;prompt='continue';sessionId=$session;claudeSessionId=$session;resume=$true;providerId='offline-m2';model='offline';attachments=@()}
    if(-not $historyPreview.historyMeasured -or [long]$historyPreview.historyInputTokens -ne 9900){throw 'Context preview did not select the newest mirrored transcript usage'}
    Remove-Item -LiteralPath $sourceTranscript -Force
    $sourceTranscript=''
    $transcriptView=Api $connection ("/api/workbench/transcripts/$session")
    if([IO.Path]::GetFullPath([string]$transcriptView.workspace) -ne [IO.Path]::GetFullPath($workspace)){throw 'Workbench did not preserve the mirror source workspace'}
    $filteredTranscript=Api $connection ("/api/workbench/transcripts/${session}?workspace="+[uri]::EscapeDataString($workspace))
    if([string]$filteredTranscript.id -ne $session){throw 'Workbench could not resolve the mirrored transcript by source workspace'}
    if(Test-Path (Join-Path $workspace 'agent-change.txt')){throw 'Agent changed the source workspace before review'}
    if(-not @($isolation.changes|Where-Object path -eq 'agent-change.txt').Count){throw 'Isolated change was not visible'}
    $read=Api $connection '/api/permissions/request' 'POST' @{jobId=$job.jobId;toolName='Read';input=@{file_path=(Join-Path $worktree 'base.txt')}};$readResult=Api $connection ("/api/permissions/result/$($read.id)")
    if(-not $read.automatic -or $readResult.decision.behavior -ne 'allow'){throw 'Safe read was not auto-allowed'}
    $delete=Api $connection '/api/permissions/request' 'POST' @{jobId=$job.jobId;toolName='Bash';input=@{command='Remove-Item -Recurse target'}}
    if($delete.state -ne 'pending'){throw 'Destructive command did not require approval'}
    Api $connection '/api/permissions/respond' 'POST' @{id=$delete.id;behavior='deny';message='stress deny'}|Out-Null;$deleteResult=Api $connection ("/api/permissions/result/$($delete.id)")
    if($deleteResult.decision.behavior -ne 'deny'){throw 'Destructive decision was not enforced'}
    $config=Get-Content -LiteralPath (Join-Path $workspace ".claude-gui-v2\runs\$($job.jobId)\permissions.mcp.json") -Raw -Encoding UTF8
    if($config.Contains($connection.Secret) -or $config.Contains('CLAUDE_GUI_PERMISSION_SECRET"')){throw 'Plaintext Host secret leaked into permission config'}
    $diff=Api $connection ("/api/workbench/task/diff?jobId=$($job.jobId)&path="+[uri]::EscapeDataString('agent-change.txt'))
    if(([string]$diff.diff).IndexOf('isolated-change',[StringComparison]::Ordinal)-lt 0){throw 'Task Diff API did not return the selected file preview'}
    $applied=Api $connection '/api/workbench/task/apply' 'POST' @{jobId=$job.jobId;paths=@('agent-change.txt')}
    if($applied.review.state -ne 'partial' -or @($applied.review.applyJournal).Count -ne 1){throw 'Partial apply did not return a durable Review journal'}
    if((Get-Content -LiteralPath (Join-Path $workspace 'agent-change.txt') -Raw -Encoding UTF8) -ne 'isolated-change'){throw 'Reviewed change was not applied'}
    if((Get-Content -LiteralPath (Join-Path $workspace 'base.txt') -Raw -Encoding UTF8) -ne 'base'){throw 'Unselected change leaked into the source workspace'}
    $discarded=Api $connection '/api/workbench/task/discard' 'POST' @{jobId=$job.jobId}
    if($discarded.review.state -ne 'applied' -or @($discarded.review.changes).Count -ne 0){throw 'Discard did not remove only the remaining isolated changes'}
    $reverted=Api $connection '/api/workbench/task/revert' 'POST' @{jobId=$job.jobId}
    if(-not $reverted.sourceReverted -or $reverted.review.state -ne 'reverted' -or (Test-Path (Join-Path $workspace 'agent-change.txt'))){throw 'Review revert did not undo the source-applied change'}
    [IO.File]::Copy($workerTranscript,(Join-Path $sourceTranscriptDirectory ($session+'.jsonl')),$true)
    $sourceTranscript=Join-Path $sourceTranscriptDirectory ($session+'.jsonl')
    $deleted=Api $connection ("/api/sessions/${session}") 'DELETE' @{workspace=$workspace;transcriptIds=@($session)}
    if([long]$deleted.transcriptsDeleted -lt 2 -or (Test-Path -LiteralPath $sourceTranscript) -or (Test-Path -LiteralPath $workerTranscript) -or (Test-Path -LiteralPath ($workerTranscript+'.workbench.json'))){throw 'Permanent delete did not remove every exact transcript mirror'}
    $sourceTranscript='';$workerTranscript=''
    [pscustomobject]@{TaskPermissionManifest='OK';SafeReadAutoAllow='OK';DestructiveApproval='OK';ProtectedMcpSecret='OK';GitWorktree='OK';ResumeAcrossIsolation='OK';TranscriptMirror='OK';LatestMirrorHistory=$historyPreview.historyInputTokens;SourceWorkspaceLookup='OK';PermanentMirrorDelete='OK';SourceUntouchedBeforeReview='OK';DiffReview='OK';PartialReviewApply='OK';DiscardRemaining='OK';UndoApplied='OK';JobId=$job.jobId;Root=$root}|Format-List
}
finally {
    Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    if($sourceTranscript -and (Test-Path -LiteralPath $sourceTranscript)){Remove-Item -LiteralPath $sourceTranscript -Force}
    if($workerTranscript -and (Test-Path -LiteralPath $workerTranscript)){Remove-Item -LiteralPath $workerTranscript -Force}
    if($workerTranscript -and (Test-Path -LiteralPath ($workerTranscript+'.workbench.json'))){Remove-Item -LiteralPath ($workerTranscript+'.workbench.json') -Force}
    if($worktree -and (Test-Path -LiteralPath $worktree)){& git -C $workspace worktree remove --force $worktree 2>$null|Out-Null}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

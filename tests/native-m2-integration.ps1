param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for M2 integration state'
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
$env:CLAUDE_GUI_WORKSPACE=$workspace;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake
$runtimePath=Join-Path $workspace '.claude-gui-v2\runtime-state.json';$hostProcess=$null;$worktree=''
try {
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $workspace -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{id='offline-m2';name='Offline M2';token='stub';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    $session=[guid]::NewGuid().ToString();$job=Api $connection '/api/chat/start' 'POST' @{workspace=$workspace;prompt='m2-write';sessionId=$session;claudeSessionId=$session;resume=$false;requestId='m2-request';providerId='offline-m2';model='offline';effort='low';permissionMode='agent';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
    $poll=Wait-Until {Api $connection ("/api/chat/poll/$($job.jobId)?after=0")} {param($v)$null -ne $v -and $v.status.state -eq 'completed'}
    $isolation=Api $connection ("/api/workbench/task/isolation?jobId=$($job.jobId)");$worktree=[string]$isolation.worktreeRoot
    if($isolation.kind -ne 'git-worktree'){throw 'Agent did not use a Git worktree'}
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
    Api $connection '/api/workbench/task/apply' 'POST' @{jobId=$job.jobId;paths=@('agent-change.txt')}|Out-Null
    if((Get-Content -LiteralPath (Join-Path $workspace 'agent-change.txt') -Raw -Encoding UTF8) -ne 'isolated-change'){throw 'Reviewed change was not applied'}
    if((Get-Content -LiteralPath (Join-Path $workspace 'base.txt') -Raw -Encoding UTF8) -ne 'base'){throw 'Unselected change leaked into the source workspace'}
    [pscustomobject]@{TaskPermissionManifest='OK';SafeReadAutoAllow='OK';DestructiveApproval='OK';ProtectedMcpSecret='OK';GitWorktree='OK';SourceUntouchedBeforeReview='OK';PartialReviewApply='OK';JobId=$job.jobId;Root=$root}|Format-List
}
finally {
    Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    if($worktree -and (Test-Path -LiteralPath $worktree)){& git -C $workspace worktree remove --force $worktree 2>$null|Out-Null}
}

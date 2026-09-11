param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25, [string]$Message = 'condition') {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 120
        try { $value = & $Action } catch { $value = $null }
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Message"
}

function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until {
        if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -ne $PreviousPid } 30 'Host runtime'
    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'} }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json; charset=utf-8'
        $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 18 -Compress))
    }
    Invoke-RestMethod @request
}

function Api-Status($Connection, [string]$Path, [string]$Method, $Body) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 18 -Compress))
    Invoke-WebRequest -Uri ($Connection.Base+$Path) -Headers $Connection.Headers -Method $Method -ContentType 'application/json; charset=utf-8' -Body $bytes -SkipHttpErrorCheck
}

function Stop-Exact([string]$Path) {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Path } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-update-handoff-' + [guid]::NewGuid().ToString('N'))))
$rollbackRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-update-rollback-' + [guid]::NewGuid().ToString('N'))))
if (-not ($root.TrimEnd('\') + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not ($rollbackRoot.TrimEnd('\') + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe update test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
[IO.Directory]::CreateDirectory($rollbackRoot) | Out-Null

$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1')
$csc = $testDependencies.Compiler
$json = $testDependencies.Json
$fake = Join-Path $root 'fake-claude.exe'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

$targetRoot = Join-Path $root 'install'
$exeName = [IO.Path]::GetFileName($Executable)
$previousName = [IO.Path]::GetFileNameWithoutExtension($Executable) + '.previous.exe'
$target = Join-Path $targetRoot $exeName
$data = Join-Path $root '.claude-gui-v2'
$stageRoot = Join-Path $data 'updates\6-4-5-test'
$staged = Join-Path $stageRoot $exeName
[IO.Directory]::CreateDirectory($targetRoot) | Out-Null
[IO.Directory]::CreateDirectory($stageRoot) | Out-Null
Copy-Item -LiteralPath $Executable -Destination $target
Copy-Item -LiteralPath $Executable -Destination $staged
$hash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
$runtimePath = Join-Path $data 'runtime-state.json'

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = $targetRoot
$env:CLAUDE_GUI_CLAUDE_EXE = $fake
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_UPDATE_TEST_NO_UI = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'update-handoff-' + [guid]::NewGuid().ToString('N')
$hostProcess = $null
$terminalCommandJob = $null
try {
    $hostProcess = Start-Process -FilePath $target -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    $badPath = Api-Status $connection '/api/workbench/update/apply' 'POST' @{path=$Executable;sha256=$hash;version='6.4.5-test'}
    if ([int]$badPath.StatusCode -ne 400 -or $badPath.Content -notmatch '受控的更新目录') { throw 'Update apply accepted a path outside the controlled update directory' }
    $badHash = Api-Status $connection '/api/workbench/update/apply' 'POST' @{path=$staged;sha256=('0' * 64);version='6.4.5-test'}
    if ([int]$badHash.StatusCode -ne 400 -or $badHash.Content -notmatch 'SHA-256') { throw 'Update apply accepted an invalid hash' }
    Api $connection '/api/providers' 'POST' @{
        id='update-fixture';name='Update Fixture';token='stub-update-token';authStyle='bearer'
        text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@('fixture-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
        capabilities=@{schemaVersion=2;evidencePolicy='fixture';models=@{'fixture-model'=@{chat=$true;tools=$true;contextWindow=128000;evidence='fixture'}}}
    } | Out-Null
    $session = [guid]::NewGuid().ToString()
    $job = Api $connection '/api/chat/start' 'POST' @{
        workspace=$root;prompt='tool-runtime-cancel update gate';attachments=@();allowedDirs=@();sessionId=$session;claudeSessionId=$session;resume=$false
        providerId='update-fixture';model='fixture-model';effort='low';maxTurns=10;permissionMode='readonly';allowedTools=@();disallowedTools=@()
    }
    Wait-Until { Api $connection '/api/bootstrap' } { param($value) @($value.activeJobs).Count -eq 1 } 15 'active update-gate Run' | Out-Null
    $blocked = Api-Status $connection '/api/workbench/update/apply' 'POST' @{path=$staged;sha256=$hash;version='6.4.5-test'}
    if ([int]$blocked.StatusCode -ne 409 -or $blocked.Content -notmatch '任务正在运行') { throw 'Active Agent did not block update apply' }
    Api $connection ("/api/chat/stop/$($job.jobId)") 'POST' @{} | Out-Null
    Wait-Until { Api $connection ("/api/chat/poll/$($job.jobId)?after=0") } { param($value) $value.status.state -in @('cancelled','failed','completed') } 20 'cancelled update-gate Run' | Out-Null
    Wait-Until { Api $connection '/api/bootstrap' } { param($value) @($value.activeJobs).Count -eq 0 } 15 'idle Host before update' | Out-Null

    $terminal = Api $connection '/api/workbench/terminal/start' 'POST' @{workspace=$root;shell='powershell'}
    $terminalUpdateStatus = Api $connection '/api/workbench/update'
    if ([int]$terminalUpdateStatus.activeTerminalBlockers -lt 1) { throw 'Update status did not expose the live terminal blocker' }
    $terminalBlocked = Api-Status $connection '/api/workbench/update/apply' 'POST' @{path=$staged;sha256=$hash;version='6.4.5-test'}
    if ([int]$terminalBlocked.StatusCode -ne 409 -or $terminalBlocked.Content -notmatch '终端') { throw 'Live ConPTY terminal did not block update apply' }
    Api $connection '/api/workbench/terminal/stop' 'POST' @{id=$terminal.id} | Out-Null
    $clearedTerminalUpdateStatus = Api $connection '/api/workbench/update'
    if ([int]$clearedTerminalUpdateStatus.activeTerminalBlockers -ne 0) { throw 'Closed terminal remained in update blockers' }

    $terminalMarker = Join-Path $root 'terminal-command-running.marker'
    $terminalBody = @{workspace=$root;command=('echo running>' + $terminalMarker + ' & ping -n 4 127.0.0.1>nul')} | ConvertTo-Json -Compress
    $terminalCommandJob = Start-Job -ScriptBlock {
        param($Base,$Secret,$JsonBody)
        $requestHeaders = @{'X-Desktop-Secret'=$Secret;'X-Workbench-Protocol'='2'}
        Invoke-RestMethod -Method Post -Uri ($Base + '/api/workbench/terminal') -Headers $requestHeaders -ContentType 'application/json; charset=utf-8' -Body $JsonBody
    } -ArgumentList $connection.Base,$connection.Headers['X-Desktop-Secret'],$terminalBody
    Wait-Until { Test-Path -LiteralPath $terminalMarker } { param($value) $value } 10 'active one-shot terminal command'
    $commandBlocked = Api-Status $connection '/api/workbench/update/apply' 'POST' @{path=$staged;sha256=$hash;version='6.4.5-test'}
    if ([int]$commandBlocked.StatusCode -ne 409 -or $commandBlocked.Content -notmatch '终端') { throw 'Active one-shot terminal command did not block update apply' }
    Wait-Job -Job $terminalCommandJob -Timeout 15 | Out-Null
    Receive-Job -Job $terminalCommandJob -ErrorAction Stop | Out-Null
    Remove-Job -Job $terminalCommandJob -Force; $terminalCommandJob = $null

    $oldPid = [int]$connection.Runtime.pid
    $accepted = Api $connection '/api/workbench/update/apply' 'POST' @{path=$staged;sha256=$hash;version='6.4.5-test'}
    if (-not [bool]$accepted.accepted) { throw 'Update handoff was not accepted' }
    $connection = Connect-Host $runtimePath $oldPid
    $result = Wait-Until {
        $path = Join-Path $data 'update-result.json'
        if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'installed' } 35 'installed update result'
    if (-not (Test-Path -LiteralPath (Join-Path $targetRoot $previousName))) { throw 'Previous EXE was not retained' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'Updated target hash mismatch' }
    $handoffPid = [int]$connection.Runtime.pid

    Stop-Exact $target
    $rbTargetRoot = Join-Path $rollbackRoot 'install'
    $rbTarget = Join-Path $rbTargetRoot $exeName
    $rbData = Join-Path $rollbackRoot '.claude-gui-v2'
    $rbStageRoot = Join-Path $rbData 'updates\6-4-5-test'
    $rbStaged = Join-Path $rbStageRoot $exeName
    [IO.Directory]::CreateDirectory($rbTargetRoot) | Out-Null
    [IO.Directory]::CreateDirectory($rbStageRoot) | Out-Null
    Copy-Item -LiteralPath $Executable -Destination $rbTarget
    Copy-Item -LiteralPath $Executable -Destination $rbStaged
    $rbHash = (Get-FileHash -LiteralPath $rbStaged -Algorithm SHA256).Hash
    $env:CLAUDE_GUI_WORKSPACE = $rollbackRoot
    $env:CLAUDE_GUI_ROOT = $rbTargetRoot
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'update-rollback-' + [guid]::NewGuid().ToString('N')
    $env:CLAUDE_GUI_UPDATE_TEST_FAIL_NEW = '1'
    $updater = Start-Process -FilePath $rbStaged -ArgumentList @('--apply-update',$rbStaged,$rbTarget,$rbHash) -WorkingDirectory $rollbackRoot -WindowStyle Hidden -PassThru
    $rbResult = Wait-Until {
        $path = Join-Path $rbData 'update-result.json'; if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'rolled-back' } 35 'audited update rollback'
    if (-not $updater.WaitForExit(5000)) { throw 'Updater process did not exit after rollback result was written' }
    if ($updater.ExitCode -ne 2) { throw "Rollback updater exit code was $($updater.ExitCode), expected 2" }
    if ($rbResult.state -ne 'rolled-back' -or $rbResult.error -notmatch 'Injected startup') { throw 'Injected startup failure did not produce an audited rollback' }
    if (-not (Test-Path -LiteralPath (Join-Path $rbTargetRoot $previousName))) { throw 'Rollback did not preserve previous EXE' }
    $rbRuntime = Wait-Until {
        $path = Join-Path $rbData 'runtime-state.json'; if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' } 25 'rolled-back Host'

    $tokenLeaked = (Get-Content -LiteralPath (Join-Path $rbData 'update-result.json') -Raw -Encoding UTF8) -match 'stub-update-token'
    if ($tokenLeaked) { throw 'Update audit leaked a Provider token' }
    [pscustomobject]@{
        UpdateHandoff='PASS';ControlledPath=$true;HashRechecked=$true;ActiveRunBlocked=$true;ActiveTerminalBlocked=$true;ActiveTerminalCommandBlocked=$true;OldHostPid=$oldPid;NewHostPid=$handoffPid
        PreviousRetained=$true;InstalledHashVerified=$true;RollbackState=$rbResult.state;RollbackHostPid=$rbRuntime.pid
        TokenLeaked=$false
    } | Format-List
}
finally {
    if ($terminalCommandJob) { Stop-Job -Job $terminalCommandJob -ErrorAction SilentlyContinue; Remove-Job -Job $terminalCommandJob -Force -ErrorAction SilentlyContinue }
    Stop-Exact $target
    if ($null -ne $rbTarget) { Stop-Exact $rbTarget }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_UPDATE_TEST_NO_UI,Env:CLAUDE_GUI_UPDATE_TEST_FAIL_NEW,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

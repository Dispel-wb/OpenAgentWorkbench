param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for compaction evidence state'
}

function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until { try { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $null } } `
        { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -ne $PreviousPid }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 16 -Compress)) }
    Invoke-RestMethod @request
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-compaction-evidence-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_CLAUDE_EXE = $fake
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'compaction-evidence-' + [guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{
        id='compaction-provider';name='Compaction Fixture';token='stub-compaction-token';authStyle='bearer'
        text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@('fixture-128k')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
        capabilities=@{schemaVersion=1;evidencePolicy='fixture';models=@{'fixture-128k'=@{contextWindow=128000;evidence='fixture'}}}
    } | Out-Null
    $session = [guid]::NewGuid().ToString()
    $job = Api $connection '/api/chat/start' 'POST' @{
        workspace=$root;prompt='compaction-event 中文压缩证据';attachments=@();allowedDirs=@();sessionId=$session;claudeSessionId=$session;resume=$false
        providerId='compaction-provider';model='fixture-128k';effort='low';permissionMode='readonly';allowedTools=@();disallowedTools=@()
    }
    $poll = Wait-Until { Api $connection ("/api/chat/poll/$($job.jobId)?after=0") } { param($value) $value.status.state -eq 'completed' }
    $evidence = Api $connection ("/api/chat/evidence/$($job.jobId)")
    $compactions = @($evidence.context.sources | Where-Object type -eq 'compaction')
    if ($compactions.Count -ne 1 -or [long]$evidence.summary.compactions -ne 1) { throw 'Compaction was not normalized into Run evidence' }
    $metadata = $compactions[0].metadata
    if ($metadata.mode -ne 'auto' -or -not [bool]$metadata.nativeAutocompact -or [long]$metadata.beforeTokens -ne 96000 -or [long]$metadata.afterTokens -ne 18000) { throw 'Compaction metadata was not preserved' }
    $payloads = @($poll.events.payload) -join "`n"
    if (-not $payloads.Contains('compact_boundary')) { throw 'Compaction event was not available to the UI stream' }
    $serialized = $evidence | ConvertTo-Json -Depth 18 -Compress
    if ($serialized.Contains('stub-compaction-token')) { throw 'Compaction evidence leaked a token' }

    $oldPid = [int]$connection.Runtime.pid
    Stop-Process -Id $oldPid -Force; Start-Sleep -Milliseconds 300
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath $oldPid
    $restored = Api $connection ("/api/chat/evidence/$($job.jobId)")
    if ([long]$restored.summary.compactions -ne 1) { throw 'Compaction evidence did not survive Host restart' }

    [pscustomobject]@{
        CompactionEvidence='PASS';NativeAutocompact=$true;BeforeTokens=$metadata.beforeTokens;AfterTokens=$metadata.afterTokens
        UiStreamEvent=$true;PersistedAfterRestart=$true;TokenLeaked=$false;Workspace=$root
    } | Format-List
}
finally {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

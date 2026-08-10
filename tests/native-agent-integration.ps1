param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; $value = & $Action; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for integration state'
}
function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until { try { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $null } } { param($v) $null -ne $v -and $v.state -eq 'running' -and [int]$v.pid -ne $PreviousPid }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $secret = [Text.Encoding]::UTF8.GetString($plain)
    return @{ Runtime = $runtime; Base = "http://127.0.0.1:$($runtime.port)"; Headers = @{ 'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2' } }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $args = @{ Uri = $Connection.Base + $Path; Headers = $Connection.Headers; Method = $Method }
    if ($null -ne $Body) { $args.ContentType = 'application/json; charset=utf-8'; $args.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress)) }
    Invoke-RestMethod @args
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-native-agent-' + [guid]::NewGuid().ToString('N'))
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
$utf8One = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('56ys5LiA5p2h5Lit5paH6ZO+6Lev'))
$utf8Recovery = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('56ys5Zub6L2u5Lit5pat5oGi5aSN'))
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
$connection = Connect-Host $runtimePath
$provider = Api $connection '/api/providers' 'POST' @{
    id='offline-native';name='Offline Native';token='stub-token';authStyle='bearer'
    text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')}
    image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
}
$sessionOne = [guid]::NewGuid().ToString(); $requestOne = [guid]::NewGuid().ToString()
$first = Api $connection '/api/chat/start' 'POST' @{workspace=$root;prompt=$utf8One;sessionId=$sessionOne;claudeSessionId=$sessionOne;resume=$false;requestId=$requestOne;providerId='offline-native';model='offline-model';effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
$firstPoll = Wait-Until { Api $connection ("/api/chat/poll/$($first.jobId)?after=0") } { param($v) $v.status.state -eq 'completed' }
if (-not (@($firstPoll.events.payload) -join "`n").Contains($utf8One)) { throw 'Chinese event did not roundtrip through SQLite' }
$duplicate = Api $connection '/api/chat/start' 'POST' @{workspace=$root;prompt='不应重复执行';sessionId=$sessionOne;claudeSessionId=$sessionOne;resume=$false;requestId=$requestOne;providerId='offline-native';model='offline-model';effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
if ($duplicate.jobId -ne $first.jobId -or -not $duplicate.idempotent) { throw 'Durable requestId did not deduplicate' }

$sessionTwo = [guid]::NewGuid().ToString(); $requestTwo = [guid]::NewGuid().ToString()
$second = Api $connection '/api/chat/start' 'POST' @{workspace=$root;prompt=$utf8Recovery;sessionId=$sessionTwo;claudeSessionId=$sessionTwo;resume=$false;requestId=$requestTwo;providerId='offline-native';model='offline-model';effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
$inputState = Join-Path $root ".claude-gui-v2\runs\$($second.jobId)\worker-input-state.json"
Wait-Until { if (Test-Path -LiteralPath $inputState) { Get-Content -LiteralPath $inputState -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($v) $null -ne $v -and [long]$v.sentOffset -gt [long]$v.completedOffset } | Out-Null
$oldHostPid = [int]$connection.Runtime.pid
Stop-Process -Id $oldHostPid -Force
Start-Sleep -Milliseconds 800
$newHost = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
$connection = Connect-Host $runtimePath $oldHostPid
if ([int]$connection.Runtime.pid -eq $oldHostPid) { throw 'Host PID did not change' }
$bootstrap = Api $connection '/api/bootstrap'
if (-not @($bootstrap.activeJobs | Where-Object id -eq $second.jobId).Count) { throw 'In-flight job was not restored' }
$recovered = Wait-Until { Api $connection ("/api/chat/poll/$($second.jobId)?after=0") } { param($v) $v.status.state -eq 'completed' } 25
$payloads = @($recovered.events.payload) -join "`n"
if (-not $payloads.Contains($utf8Recovery)) { throw 'Recovered input was not completed' }
$resultCount = @($recovered.events | Where-Object { $_.payload -match '"type":"result"' }).Count
if ($resultCount -ne 1) { throw "Recovered run produced duplicate results: $resultCount" }
$health = $bootstrap.persistence
if ($health.journalMode -ne 'wal' -or $health.integrity -ne 'ok') { throw 'SQLite health check failed' }
Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
[pscustomobject]@{
    NativeWorker = 'OK'; SQLiteEventSource = 'OK'; ChineseRoundTrip = 'OK'; DurableRequestId = 'OK'
    OldHostPid = $oldHostPid; NewHostPid = $connection.Runtime.pid; InFlightRecovery = 'OK'; DuplicateResults = $resultCount
    Workspace = $root
} | Format-List

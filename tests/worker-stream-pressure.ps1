param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateRange(100, 10000)][int]$Events = 3000
)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-stream-pressure-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$fake = Join-Path $testRoot 'fake-claude.exe'
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $testRoot 'Newtonsoft.Json.dll')

function Wait-Runtime([string]$Path) {
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $value = $null }
    } while (($null -eq $value -or $value.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $value -or $value.state -ne 'running') { throw 'Stream pressure Host did not start' }
    return $value
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri = $Connection.Base + $Path; Headers = $Connection.Headers; Method = $Method; TimeoutSec = 20 }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json; charset=utf-8'
        $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 16 -Compress))
    }
    Invoke-RestMethod @request
}

$env:CLAUDE_GUI_WORKSPACE = $testRoot
$env:CLAUDE_GUI_ROOT = (Join-Path $testRoot 'empty-install')
$env:CLAUDE_GUI_CLAUDE_EXE = $fake
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'stream-pressure-' + [guid]::NewGuid().ToString('N')
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $runtime = Wait-Runtime (Join-Path $testRoot '.claude-gui-v2\runtime-state.json')
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $connection = @{ Base = "http://127.0.0.1:$($runtime.port)"; Headers = @{'X-Desktop-Secret' = [Text.Encoding]::UTF8.GetString($plain); 'X-Workbench-Protocol' = '2'} }
    Api $connection '/api/providers' 'POST' @{
        id = 'offline-stream'; name = 'Offline Stream'; token = 'sk-offline-stream-only'; authStyle = 'bearer'
        text = @{ enabled = $true; protocol = 'anthropic'; baseUrl = 'http://127.0.0.1:9'; models = @('offline-model') }
        image = @{ enabled = $false; protocol = 'openai-images'; baseUrl = ''; models = @() }
    } | Out-Null
    $session = [guid]::NewGuid().ToString()
    $startedAt = Get-Date
    $run = Api $connection '/api/chat/start' 'POST' @{
        workspace = $testRoot; prompt = "stream-stress:$Events"; sessionId = $session; claudeSessionId = $session; resume = $false
        requestId = 'stream-pressure-' + [guid]::NewGuid().ToString('N'); providerId = 'offline-stream'; model = 'offline-model'
        effort = 'low'; permissionMode = 'readonly'; attachments = @(); allowedDirs = @(); allowedTools = @(); disallowedTools = @()
    }
    $cursor = 0L
    $received = 0
    $lastState = 'running'
    $expires = (Get-Date).AddSeconds(45)
    do {
        $poll = Api $connection "/api/chat/poll/$($run.jobId)?after=$cursor"
        foreach ($event in @($poll.events)) {
            $expected = $cursor + 1
            if ([long]$event.seq -ne $expected) { throw "Non-contiguous event stream: expected $expected, got $($event.seq)" }
            $cursor = [long]$event.seq
            $received++
        }
        if ([long]$poll.nextSeq -gt $cursor) { $cursor = [long]$poll.nextSeq }
        $lastState = [string]$poll.status.state
        if ($lastState -eq 'failed') { throw "Stream pressure Run failed: $($poll.error)" }
        if ($lastState -ne 'completed') { Start-Sleep -Milliseconds 25 }
    } while ($lastState -ne 'completed' -and (Get-Date) -lt $expires)
    if ($lastState -ne 'completed') { throw 'Stream pressure Run did not complete' }
    if ($received -ne $Events + 1) { throw "Expected $($Events + 1) events, received $received" }
    $elapsed = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 2)
    $metrics = Api $connection '/api/workbench/metrics'
    [pscustomobject]@{
        WorkerStreamPressure = 'PASS'
        RequestedEvents = $Events
        ReceivedEvents = $received
        FinalCursor = $cursor
        ElapsedSeconds = $elapsed
        HttpErrors = [int]$metrics.http.errors
        WorkerErrors = [int]$metrics.host.workerErrors
        RunId = $run.jobId
    } | Format-List
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $testRoot) -and $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

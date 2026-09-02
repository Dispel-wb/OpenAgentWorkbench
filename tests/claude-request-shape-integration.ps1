param(
    [string]$RuntimeFile = 'D:\work\Claude\.claude-gui-v2\runtime-state.json',
    [string]$Workspace = 'D:\work\Claude',
    [string]$Executable = ''
)
$ErrorActionPreference = 'Stop'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-request-shape-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$portFile = Join-Path $root 'port.txt'
$stateFile = Join-Path $root 'state.json'
$fixture = $null
$providerId = 'local-request-capture'
$base = ''
$headers = $null
$hostProcess = $null

function Api([string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri = $base + $Path; Method = $Method; Headers = $headers; TimeoutSec = 10 }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json; charset=utf-8'
        $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 16 -Compress))
    }
    Invoke-RestMethod @request
}

try {
    $node = 'C:\Program Files\nodejs\node.exe'
    $fixture = Start-Process -FilePath $node -ArgumentList @((Join-Path $PSScriptRoot 'adapter-resilience-fixture.js'), $portFile, $stateFile) -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $expires = (Get-Date).AddSeconds(10)
    while (-not (Test-Path -LiteralPath $portFile) -and (Get-Date) -lt $expires) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $portFile)) { throw 'Capture fixture failed to start' }
    $fixturePort = [int](Get-Content -LiteralPath $portFile -Raw)

    if ($Executable) {
        $Workspace = $root
        $RuntimeFile = Join-Path $root '.claude-gui-v2\runtime-state.json'
        $env:CLAUDE_GUI_WORKSPACE = $root
        $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
        $env:CLAUDE_GUI_CLAUDE_EXE = 'D:\softwares\ClaudeCode\node_modules\@anthropic-ai\claude-code\bin\claude.exe'
        $env:CLAUDE_GUI_TEST_MODE = '1'
        $env:CLAUDE_GUI_MUTEX_SCOPE = 'request-shape-' + [guid]::NewGuid().ToString('N')
        $hostProcess = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
        $expires = (Get-Date).AddSeconds(15)
        while (-not (Test-Path -LiteralPath $RuntimeFile) -and (Get-Date) -lt $expires) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $RuntimeFile)) { throw 'Isolated capture Host failed to start' }
    }

    $runtime = Get-Content -LiteralPath $RuntimeFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $base = "http://127.0.0.1:$($runtime.port)"
    $headers = @{'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2'}
    Api '/api/providers' 'POST' @{
        id = $providerId; name = 'Local request capture'; token = 'stub-capture-token'; authStyle = 'bearer'
        text = @{ enabled = $true; protocol = 'openai'; baseUrl = "http://127.0.0.1:$fixturePort/v1"; models = @('Qwen/Qwen3-8B') }
        image = @{ enabled = $false; protocol = 'openai-images'; baseUrl = ''; models = @() }
    } | Out-Null

    $session = [guid]::NewGuid().ToString()
    $requestId = 'capture-' + [guid]::NewGuid().ToString('N')
    $run = Api '/api/chat/start' 'POST' @{
        workspace = $Workspace; prompt = 'reply with local request shape ok'; sessionId = $session; claudeSessionId = $session; resume = $false
        requestId = $requestId; providerId = $providerId; model = 'Qwen/Qwen3-8B'
        effort = 'low'; permissionMode = 'readonly'; maxTurns = 10; attachments = @(); allowedDirs = @(); allowedTools = @(); disallowedTools = @()
    }
    $cursor = 0L
    $state = 'running'
    $expires = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 150
        $poll = Api "/api/chat/poll/$($run.jobId)?after=$cursor"
        $cursor = [long]$poll.nextSeq
        $state = if ($poll.status.terminalState) { [string]$poll.status.terminalState } else { [string]$poll.status.state }
    } while ($state -notin @('completed','failed','cancelled') -and (Get-Date) -lt $expires)
    if ($state -notin @('completed','failed','cancelled')) { Api "/api/chat/stop/$($run.jobId)" 'POST' @{} | Out-Null }

    $capture = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $shapeResult = if ($state -eq 'completed') { 'PASS' } else { 'FAIL' }
    [pscustomobject]@{
        ClaudeRequestShape = $shapeResult
        RunState = $state
        RequestedModel = $capture.lastRequest.model
        RequestedModels = @($capture.requestModels) -join ','
        BodyBytes = $capture.lastRequest.bodyBytes
        Keys = $capture.lastRequest.keys -join ','
        MessageCount = $capture.lastRequest.messageCount
        MessageShape = $capture.lastRequest.messageShape | ConvertTo-Json -Depth 6 -Compress
        ToolCount = $capture.lastRequest.toolCount
        Tools = $capture.lastRequest.tools | ConvertTo-Json -Depth 12 -Compress
        Stream = $capture.lastRequest.stream
        HasStreamOptions = $capture.lastRequest.hasStreamOptions
        MaxTokens = $capture.lastRequest.maxTokens
        RequestCount = $capture.requests
    } | Format-List
    if ($state -ne 'completed') { throw 'Claude request shape fixture did not complete' }
    if (@($capture.requestModels | Where-Object { $_ -ne 'Qwen/Qwen3-8B' }).Count -gt 0) { throw 'Claude model aliases escaped the selected UI model' }
}
finally {
    try { if ($base -and $headers) { Invoke-RestMethod -Uri ($base + '/api/providers/' + $providerId) -Method DELETE -Headers $headers -TimeoutSec 5 | Out-Null } } catch { }
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

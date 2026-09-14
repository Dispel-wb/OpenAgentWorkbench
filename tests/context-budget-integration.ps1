param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 80
        try { $value = & $Action } catch { $value = $null }
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for Context budget integration state'
}

function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until {
        if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json }
    } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$runtime.authProtected), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{
        Runtime = $runtime
        Base = "http://127.0.0.1:$($runtime.port)"
        Headers = @{
            'X-Desktop-Secret' = [Text.Encoding]::UTF8.GetString($plain)
            'X-Workbench-Protocol' = '2'
        }
    }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri = $Connection.Base + $Path; Headers = $Connection.Headers; Method = $Method }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json; charset=utf-8'
        $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 16 -Compress))
    }
    Invoke-RestMethod @request
}

function RejectedRequest($Connection, [string]$Path, $Body) {
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object Net.Http.HttpClient
    try {
        foreach ($entry in $Connection.Headers.GetEnumerator()) { $client.DefaultRequestHeaders.Add($entry.Key, [string]$entry.Value) }
        $json = $Body | ConvertTo-Json -Depth 16 -Compress
        $content = New-Object Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
        try {
            $response = $client.PostAsync(($Connection.Base + $Path), $content).GetAwaiter().GetResult()
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            return @{ Status = [int]$response.StatusCode; Body = ($text | ConvertFrom-Json) }
        }
        finally { $content.Dispose() }
    }
    finally { $client.Dispose() }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-context-budget-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1')
$csc = $testDependencies.Compiler
$json = $testDependencies.Json
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_CLAUDE_EXE = $fake
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'context-budget-' + [guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
$historyTranscript = $null

try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{
        id = 'offline-budget'; name = 'Offline Budget'; token = 'stub-token'; authStyle = 'bearer'
        text = @{ enabled = $true; protocol = 'anthropic'; baseUrl = 'http://127.0.0.1:9'; models = @('budget-model') }
        image = @{ enabled = $false; protocol = 'openai-images'; baseUrl = ''; models = @() }
        capabilities = @{
            schemaVersion = 1; evidencePolicy = 'fixture'
            models = @{ 'budget-model' = @{ contextWindow = 32768; maxOutputTokens = 4096; evidence = 'fixture' } }
        }
    } | Out-Null

    $chinese = [string]([char]0x4E2D) + [char]0x6587 + [char]0x9884 + [char]0x7B97
    $smallRequest = @{
        workspace = $root; prompt = $chinese; attachments = @(); sessionId = [guid]::NewGuid().ToString()
        claudeSessionId = [guid]::NewGuid().ToString(); resume = $false; providerId = 'offline-budget'; model = 'budget-model'
        effort = 'low'; permissionMode = 'readonly'; allowedDirs = @(); allowedTools = @(); disallowedTools = @()
    }
    $smallPreview = Api $connection '/api/chat/context-preview' 'POST' $smallRequest
    if ($smallPreview.decision -ne 'pass' -or $smallPreview.capacityEvidence -ne 'provider-metadata') { throw 'Small Context preview was not accepted with provider evidence' }
    if ([long]$smallPreview.inputBudgetTokens -ne 24576) { throw "Unexpected input budget: $($smallPreview.inputBudgetTokens)" }

    $historySession = [guid]::NewGuid().ToString()
    $workspaceKey = -join ([IO.Path]::GetFullPath($root).ToCharArray() | ForEach-Object {
        if ([char]::IsLetterOrDigit($_) -or $_ -eq '-' -or $_ -eq '_') { $_ } else { '-' }
    })
    $historyDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) ('.claude\projects\' + $workspaceKey)
    [IO.Directory]::CreateDirectory($historyDirectory) | Out-Null
    $historyTranscript = Join-Path $historyDirectory ($historySession + '.jsonl')
    $historyEntry = @{
        type = 'assistant'; cwd = $root; timestamp = [DateTime]::UtcNow.ToString('o')
        message = @{
            id = [guid]::NewGuid().ToString(); model = 'budget-model'; content = @()
            usage = @{ input_tokens = 1000; output_tokens = 500; cache_read_input_tokens = 12000; cache_creation_input_tokens = 0 }
        }
    } | ConvertTo-Json -Depth 12 -Compress
    [IO.File]::WriteAllText($historyTranscript, $historyEntry + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $historyRequest = @{} + $smallRequest
    $historyRequest.sessionId = $historySession
    $historyRequest.claudeSessionId = $historySession
    $historyRequest.resume = $true
    $historyPreview = Api $connection '/api/chat/context-preview' 'POST' $historyRequest
    if (-not $historyPreview.historyMeasured -or $historyPreview.historyUnknown) { throw 'Resume history was not measured from transcript usage evidence' }
    if ($historyPreview.historyEvidence -ne 'transcript-provider-usage' -or [long]$historyPreview.historyInputTokens -ne 13500) {
        throw "Unexpected history evidence: $($historyPreview | ConvertTo-Json -Depth 10 -Compress)"
    }
    if ($historyPreview.decision -eq 'blocked' -or $historyPreview.historyHardBlock) { throw 'Measured history incorrectly caused a hard block' }

    $largeRequest = @{} + $smallRequest
    $largeRequest.sessionId = [guid]::NewGuid().ToString()
    $largeRequest.claudeSessionId = $largeRequest.sessionId
    $largeRequest.prompt = 'x' * 110000
    $largePreview = Api $connection '/api/chat/context-preview' 'POST' $largeRequest
    if ($largePreview.decision -ne 'blocked' -or -not $largePreview.hardLimit) { throw 'Oversized Context preview was not blocked' }

    $runsBefore = [long](Api $connection '/api/bootstrap').persistence.runs
    $rejected = RejectedRequest $connection '/api/chat/start' $largeRequest
    $runsAfter = [long](Api $connection '/api/bootstrap').persistence.runs
    if ($rejected.Status -ne 413 -or $rejected.Body.contextBudget.decision -ne 'blocked') { throw "Backend did not enforce the Context budget with HTTP 413: status=$($rejected.Status), body=$($rejected.Body | ConvertTo-Json -Depth 8 -Compress)" }
    if ($runsAfter -ne $runsBefore) { throw 'Blocked Context created a durable Run before rejection' }

    $started = Api $connection '/api/chat/start' 'POST' $smallRequest
    $poll = Wait-Until { Api $connection ("/api/chat/poll/$($started.jobId)?after=0") } { param($value) $value.status.state -eq 'completed' }
    $evidence = Api $connection ("/api/chat/evidence/$($started.jobId)")
    if ($evidence.context.budget.decision -ne 'pass') { throw 'Accepted Run did not persist its budget decision' }
    if (-not @($evidence.context.sources | Where-Object type -eq 'context-budget').Count) { throw 'Context budget ledger entry is missing' }
    if (($evidence | ConvertTo-Json -Depth 20 -Compress).Contains('stub-token')) { throw 'Provider token leaked into Context evidence' }

    [pscustomobject]@{
        ContextPreview = 'OK'; ProviderWindowEvidence = 'OK'; InputBudgetTokens = $smallPreview.inputBudgetTokens
        OversizedDecision = $largePreview.decision; BackendStatus = $rejected.Status; BlockedRunCreated = $false
        PersistedDecision = $evidence.context.budget.decision; TokenLeaked = $false; Chinese = $smallRequest.prompt -eq $chinese
        HistoryMeasured = $historyPreview.historyMeasured; HistoryTokens = $historyPreview.historyInputTokens
        HistoryEvidence = $historyPreview.historyEvidence; HistoryHardBlock = $historyPreview.historyHardBlock
        Workspace = $root
    } | Format-List
}
finally {
    if ($hostProcess) {
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$($hostProcess.Id)" -ErrorAction SilentlyContinue
        if ($null -ne $candidate -and [string]::Equals([string]$candidate.ExecutablePath,$Executable,[StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    }
    Get-CimInstance Win32_Process | Where-Object { [string]::Equals([string]$_.ExecutablePath,$fake,[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($historyTranscript -and (Test-Path -LiteralPath $historyTranscript)) { Remove-Item -LiteralPath $historyTranscript -Force }
    if ($historyDirectory -and (Test-Path -LiteralPath $historyDirectory) -and -not (Get-ChildItem -LiteralPath $historyDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $historyDirectory -Force
    }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

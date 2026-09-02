param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for model capability integration state'
}

function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } `
        { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -ne $PreviousPid }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 20 -Compress)) }
    Invoke-RestMethod @request
}

function RejectedRequest($Connection, [string]$Path, $Body) {
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object Net.Http.HttpClient
    try {
        foreach ($entry in $Connection.Headers.GetEnumerator()) { $client.DefaultRequestHeaders.Add($entry.Key, [string]$entry.Value) }
        $content = New-Object Net.Http.StringContent(($Body|ConvertTo-Json -Depth 20 -Compress), [Text.Encoding]::UTF8, 'application/json')
        try {
            $response = $client.PostAsync(($Connection.Base+$Path), $content).GetAwaiter().GetResult()
            return @{ Status=[int]$response.StatusCode; Body=($response.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json) }
        } finally { $content.Dispose() }
    } finally { $client.Dispose() }
}

$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-capability-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe capability fixture root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$portFile = Join-Path $root 'fixture-port.txt'
$node = (Get-Command node -ErrorAction Stop).Source
$fixture = Start-Process -FilePath $node -ArgumentList @((Join-Path $PSScriptRoot 'model-capability-fixture.js'), $portFile) -WorkingDirectory $root -WindowStyle Hidden -PassThru
$port = Wait-Until { if (Test-Path -LiteralPath $portFile) { [int](Get-Content -LiteralPath $portFile -Raw -Encoding UTF8) } } { param($value) [int]$value -gt 0 }

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'model-capability-' + [guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    $discovered = Api $connection '/api/providers/discover' 'POST' @{token='fixture-token';baseUrl="http://127.0.0.1:$port/v1";authStyle='bearer'}
    $rich = $discovered.capabilities.'vision-tools-128k'
    $plain = $discovered.capabilities.'text-only-32k'
    $unknown = $discovered.capabilities.'id-only-model'
    if (-not [bool]$rich.vision -or -not [bool]$rich.tools -or [long]$rich.contextWindow -ne 131072 -or [long]$rich.maxOutputTokens -ne 8192) { throw 'Rich model metadata was not normalized' }
    if ([bool]$plain.vision -or [bool]$plain.tools -or [long]$plain.contextWindow -ne 32768 -or [long]$plain.maxOutputTokens -ne 4096) { throw 'Explicit unsupported model metadata was not preserved' }
    if ($null -ne $unknown.vision -or $null -ne $unknown.tools -or $null -ne $unknown.contextWindow) { throw 'ID-only model was incorrectly guessed as verified capability' }
    if (@($discovered.textModels) -notcontains 'dual-output-model' -or @($discovered.imageModels) -notcontains 'dual-output-model') { throw 'Dual text/image model was forced into only one capability list' }
    if (@($discovered.textModels) -contains 'image-only-model' -or @($discovered.imageModels) -notcontains 'image-only-model') { throw 'Image-only model was incorrectly exposed as a Chat model' }

    $saved = Api $connection '/api/providers' 'POST' @{
        id='capability-fixture';name='Capability Fixture';token='fixture-token';authStyle='bearer'
        text=@{enabled=$true;protocol='anthropic';baseUrl="http://127.0.0.1:$port/v1";models=$discovered.textModels}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
        capabilities=@{schemaVersion=2;evidencePolicy='endpoint-metadata-not-name-guess';models=$discovered.capabilities}
    }
    $preview = Api $connection '/api/chat/context-preview' 'POST' @{workspace=$root;prompt='中文能力矩阵';attachments=@();sessionId=[guid]::NewGuid().ToString();claudeSessionId=[guid]::NewGuid().ToString();resume=$false;providerId='capability-fixture';model='vision-tools-128k'}
    if ($preview.capacityEvidence -ne 'provider-metadata' -or [long]$preview.contextWindow -ne 131072 -or [long]$preview.inputBudgetTokens -ne 106880) { throw 'Context Planner did not consume capability metadata' }
    $blockedSession = [guid]::NewGuid().ToString()
    $blocked = RejectedRequest $connection '/api/chat/start' @{workspace=$root;prompt='agent mode';attachments=@();sessionId=$blockedSession;claudeSessionId=$blockedSession;resume=$false;providerId='capability-fixture';model='text-only-32k';permissionMode='agent'}
    if ($blocked.Status -ne 400 -or $blocked.Body.capability -ne 'tools') { throw 'Explicit Tool unsupported evidence did not block Agent mode' }

    $oldPid = [int]$connection.Runtime.pid
    Stop-Process -Id $oldPid -Force
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath $oldPid
    $providers = Api $connection '/api/providers'
    $restored = @($providers | Where-Object id -eq 'capability-fixture')[0]
    if ([long]$restored.capabilities.models.'vision-tools-128k'.contextWindow -ne 131072) { throw 'Capability matrix did not survive Host restart' }
    $publicJson = $providers | ConvertTo-Json -Depth 20 -Compress
    if ($publicJson.Contains('fixture-token')) { throw 'Capability matrix endpoint leaked the Provider token' }

    [pscustomobject]@{
        ModelCapabilityMatrix='PASS';Vision=$rich.vision;Tools=$rich.tools;ContextWindow=$rich.contextWindow
        MaxOutputTokens=$rich.maxOutputTokens;UnknownRemainsUnknown=$null -eq $unknown.vision
        ContextPlannerBudget=$preview.inputBudgetTokens;UnsupportedAgentBlocked=$blocked.Status;DualCapabilityPreserved=$true;ImageOnlyExcludedFromChat=$true;PersistedAfterRestart=$true;TokenLeaked=$false;Workspace=$root
    } | Format-List
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}

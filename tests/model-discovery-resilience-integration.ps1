param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('model-discovery-resilience-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$portFile = Join-Path $root 'fixture-port.txt'
$fixture = $null
$hostProcess = $null
$token = 'sk-model-discovery-fixture-12345678'

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 15) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for model discovery resilience state'
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method; TimeoutSec=10 }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 20 -Compress)) }
    Invoke-RestMethod @request
}

function Rejected($Connection, [string]$Path, $Body) {
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    try {
        foreach ($entry in $Connection.Headers.GetEnumerator()) { $client.DefaultRequestHeaders.Add($entry.Key, [string]$entry.Value) }
        $content = New-Object Net.Http.StringContent(($Body|ConvertTo-Json -Depth 20 -Compress), [Text.Encoding]::UTF8, 'application/json')
        try {
            $response = $client.PostAsync(($Connection.Base+$Path), $content).GetAwaiter().GetResult()
            return @{ Status=[int]$response.StatusCode; Body=($response.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json) }
        } finally { $content.Dispose() }
    } finally { $client.Dispose() }
}

try {
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixture = Start-Process -FilePath $node -ArgumentList @((Join-Path $PSScriptRoot 'model-discovery-resilience-fixture.js'), $portFile) -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $port = Wait-Until { if (Test-Path -LiteralPath $portFile) { [int](Get-Content -LiteralPath $portFile -Raw -Encoding UTF8) } } { param($value) [int]$value -gt 0 }

    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'model-discovery-resilience-' + [guid]::NewGuid().ToString('N')
    $env:CLAUDE_GUI_MODEL_DISCOVERY_TIMEOUT_SECONDS = '2'
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
    $runtime = Wait-Until { if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $connection = @{ Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }

    $discovered = Api $connection '/api/providers/discover' 'POST' @{token=$token;baseUrl="http://127.0.0.1:$port/v1";authStyle='auto'}
    if ($discovered.authStyle -ne 'x-api-key') { throw 'Automatic authentication did not resolve to x-api-key' }
    if (@($discovered.models) -notcontains 'nested-chat-model' -or [long]$discovered.capabilities.'nested-chat-model'.contextWindow -ne 65536) { throw 'Nested model list response was not normalized' }

    Api $connection '/api/providers' 'POST' @{
        id='discovery-auto';name='Discovery Auto';token=$token;authStyle='auto'
        text=@{enabled=$true;protocol='openai';baseUrl="http://127.0.0.1:$port/v1";models=@('nested-chat-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | Out-Null
    $validated = Api $connection '/api/providers/validate-model' 'POST' @{providerId='discovery-auto';model='nested-chat-model'}
    if ($validated.authStyle -ne 'x-api-key' -or $validated.provider.authStyle -ne 'x-api-key') { throw 'Live model validation did not persist the resolved x-api-key auth style' }
    $payload = @{model='nested-chat-model';max_tokens=16;stream=$true;messages=@(@{role='user';content='中文鉴权测试'})} | ConvertTo-Json -Depth 10 -Compress
    $stream = Invoke-WebRequest -UseBasicParsing -Uri ($connection.Base+'/adapter/discovery-auto/v1/messages') -Method POST -Headers @{Authorization='Bearer '+$token} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 10
    if (-not $stream.Content.Contains('自动鉴权正常：nested-chat-model')) { throw 'Resolved x-api-key authentication was not used by the Chat Adapter' }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $timeout = Rejected $connection '/api/providers/discover' @{token=$token;baseUrl="http://127.0.0.1:$port/hang";authStyle='bearer'}
    $timer.Stop()
    if ($timeout.Status -ne 400 -or -not ([string]$timeout.Body.error).Contains('超时') -or $timer.ElapsedMilliseconds -gt 5000) { throw 'Hanging model discovery was not cancelled promptly' }
    if (($timeout.Body|ConvertTo-Json -Depth 10 -Compress).Contains($token)) { throw 'Model discovery error leaked the API key' }

    $invalid = Rejected $connection '/api/providers' @{
        id='invalid-endpoint';name='Invalid';token=$token;authStyle='bearer'
        text=@{enabled=$true;protocol='openai';baseUrl='ftp://example.invalid/v1';models=@('x')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    }
    if ($invalid.Status -ne 400 -or -not ([string]$invalid.Body.error).Contains('HTTP(S)')) { throw 'Invalid Provider endpoint was accepted' }

    [pscustomobject]@{
        ModelDiscoveryResilience='PASS';ResolvedAuth=$discovered.authStyle;NestedModels=@($discovered.models).Count
        ChatAdapterUsedResolvedAuth=$true;ValidationPersistedAuth=$validated.authStyle;TimeoutMs=$timer.ElapsedMilliseconds;TokenLeaked=$false;InvalidEndpointBlocked=$invalid.Status
    } | Format-List
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_MODEL_DISCOVERY_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

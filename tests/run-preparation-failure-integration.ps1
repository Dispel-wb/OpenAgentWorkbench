param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
Add-Type -AssemblyName System.Net.Http

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for run preparation failure fixture'
}
function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress)) }
    Invoke-RestMethod @request
}
function RejectedRequest($Connection, [string]$Path, $Body) {
    $client = New-Object Net.Http.HttpClient
    try {
        foreach ($entry in $Connection.Headers.GetEnumerator()) { $client.DefaultRequestHeaders.Add($entry.Key, [string]$entry.Value) }
        $content = New-Object Net.Http.StringContent(($Body|ConvertTo-Json -Depth 16 -Compress), [Text.Encoding]::UTF8, 'application/json')
        try {
            $response = $client.PostAsync(($Connection.Base+$Path), $content).GetAwaiter().GetResult()
            return @{ Status=[int]$response.StatusCode; Body=($response.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json) }
        } finally { $content.Dispose() }
    } finally { $client.Dispose() }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-run-preparation-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe run preparation fixture root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

$env:CLAUDE_GUI_WORKSPACE=$root
$env:CLAUDE_GUI_ROOT=(Join-Path $root 'empty-install')
$env:CLAUDE_GUI_CLAUDE_EXE=$fake
$env:CLAUDE_GUI_TEST_MODE='1'
$env:CLAUDE_GUI_TEST_FAIL_RUN_PREPARATION='after-run-created'
$env:CLAUDE_GUI_MUTEX_SCOPE='run-preparation-'+[guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{
        id='offline-preparation';name='Offline Preparation';token='fixture-secret';authStyle='bearer'
        text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@('offline-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | Out-Null
    $session = [guid]::NewGuid().ToString()
    $failure = RejectedRequest $connection '/api/chat/start' @{
        workspace=$root;prompt='启动前故障状态收口';sessionId=$session;claudeSessionId=$session;resume=$false
        requestId=('preparation:'+[guid]::NewGuid().ToString('N'));providerId='offline-preparation';model='offline-model'
        effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()
    }
    if ($failure.Status -ne 500 -or $failure.Body.code -ne 'agent_preparation_failed') { throw 'Preparation failure did not return the structured terminal error' }
    $run = Get-ChildItem -LiteralPath (Join-Path $root '.claude-gui-v2\runs') -Directory | Select-Object -First 1
    if ($null -eq $run) { throw 'Prepared Run directory was not retained for audit' }
    $status = Get-Content -LiteralPath (Join-Path $run.FullName 'status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $state = Get-Content -LiteralPath (Join-Path $run.FullName 'job-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($status.state -ne 'failed' -or $status.preflightStage -ne 'run-preparation' -or $state.state -ne 'failed') { throw 'Prepared Run was left in a non-terminal state' }
    $bootstrap = Api $connection '/api/bootstrap'
    if ([int]$bootstrap.activeJobs.Count -ne 0 -or [int]$connection.Runtime.activeJobs -ne 0) { throw 'Prepared failure leaked an active in-memory job' }
    $evidence = ($status|ConvertTo-Json -Depth 12 -Compress)
    if ($evidence.Contains('fixture-secret')) { throw 'Preparation failure evidence leaked the Provider secret' }
    [pscustomobject]@{RunPreparationFailure='PASS';HttpStatus=$failure.Status;DiskState=$status.state;RuntimeState=$state.state;PreflightStage=$status.preflightStage;ActiveJobs=0;SecretLeaked=$false;RunId=$run.Name;Workspace=$root} | Format-List
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_TEST_FAIL_RUN_PREPARATION,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

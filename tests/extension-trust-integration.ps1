param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
Add-Type -AssemblyName System.Net.Http

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw ('Timed out waiting for extension trust integration state: ' + [string]$script:stage)
}
function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}
function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 14 -Compress)) }
    Invoke-RestMethod @request
}
function Post-Raw($Connection, [string]$Path, $Body) {
    $client = New-Object Net.Http.HttpClient
    try {
        foreach ($entry in $Connection.Headers.GetEnumerator()) { $client.DefaultRequestHeaders.Add($entry.Key, [string]$entry.Value) }
        $content = New-Object Net.Http.StringContent(($Body|ConvertTo-Json -Depth 14 -Compress), [Text.Encoding]::UTF8, 'application/json')
        try { $response=$client.PostAsync($Connection.Base+$Path,$content).GetAwaiter().GetResult();$text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult();return @{Status=[int]$response.StatusCode;Body=($text|ConvertFrom-Json)} }
        finally { $content.Dispose() }
    } finally { $client.Dispose() }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-extension-trust-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$skillRoot = Join-Path $root '.claude\skills\repo-review'; [IO.Directory]::CreateDirectory($skillRoot) | Out-Null
$skillFile = Join-Path $skillRoot 'SKILL.md'
[IO.File]::WriteAllText($skillFile,"---`nname: repo-review`ndescription: review repository files`n---`nPROJECT_SKILL_BODY`n",[Text.UTF8Encoding]::new($false))
$agentRoot = Join-Path $root '.claude\agents'; [IO.Directory]::CreateDirectory($agentRoot) | Out-Null
$agentFile = Join-Path $agentRoot 'repo-agent.md'
[IO.File]::WriteAllText($agentFile,"---`nname: repo-agent`ndescription: repository agent`n---`nPROJECT_AGENT_BODY`n",[Text.UTF8Encoding]::new($false))
$claudeFile = Join-Path $root 'CLAUDE.md'
[IO.File]::WriteAllText($claudeFile,"# Trusted project instructions`nPROJECT_INSTRUCTION_BODY`n",[Text.UTF8Encoding]::new($false))
$mcpFile = Join-Path $root '.mcp.json'
[IO.File]::WriteAllText($mcpFile,'{"mcpServers":{"fixture":{"command":"fixture-mcp.exe","args":["--stdio"],"env":{"MCP_SECRET":"fixture-secret-must-not-leak"}}}}',[Text.UTF8Encoding]::new($false))
$fake = Join-Path $root 'fake-claude.exe'; $vsRoot='C:\Program Files\Microsoft Visual Studio\2022\Community';$csc=Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe';$json=Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs'); if($LASTEXITCODE -ne 0){throw 'Fake worker build failed'}; Copy-Item $json (Join-Path $root 'Newtonsoft.Json.dll')
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='extension-trust-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json';$hostProcess=$null
try {
    $script:stage='Host startup';$hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{id='offline-trust';name='Offline Trust';token='stub-trust-secret';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}} | Out-Null
    $extensions=Api $connection ('/api/workbench/extensions?workspace='+[uri]::EscapeDataString($root))
    if(-not $extensions.trust.blocked -or [int]$extensions.trust.inactiveCount -ne 2){throw 'Unmanaged project extensions were not quarantined'}
    if(@($extensions.controls|Where-Object relativePath -eq 'CLAUDE.md'|Where-Object contained -eq $true).Count -ne 1 -or
       @($extensions.controls|Where-Object relativePath -eq '.mcp.json'|Where-Object kind -eq 'mcp').Count -ne 1){throw 'Claude Code control surfaces were not contained'}
    if(@($extensions.skills|Where-Object name -eq 'repo-review'|Where-Object active -eq $false).Count -ne 1){throw 'Unmanaged project Skill was reported active'}
    $request=@{workspace=$root;prompt='trust-boundary';sessionId=[guid]::NewGuid().ToString();providerId='offline-trust';model='offline';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
    $blocked=Post-Raw $connection '/api/chat/start' $request
    if($blocked.Status -ne 409 -or $blocked.Body.code -ne 'untrusted_project_extensions'){throw 'Backend did not block untrusted project extensions'}
    Api $connection '/api/workbench/extensions/trust' 'POST' @{workspace=$root;type='skill';name='repo-review';trusted=$true}|Out-Null
    Api $connection '/api/workbench/extensions/trust' 'POST' @{workspace=$root;type='agent';name='repo-agent';trusted=$true}|Out-Null
    Api $connection '/api/workbench/extensions/trust' 'POST' @{workspace=$root;type='control';name='CLAUDE.md';trusted=$true}|Out-Null
    Api $connection '/api/workbench/extensions/trust' 'POST' @{workspace=$root;type='control';name='.mcp.json';trusted=$true}|Out-Null
    $trusted=Api $connection ('/api/workbench/extensions?workspace='+[uri]::EscapeDataString($root))
    if($trusted.trust.blocked){throw 'Trusted project extensions remained quarantined'}
    if(@($trusted.controls|Where-Object relativePath -eq '.mcp.json'|Where-Object active -eq $true).Count -ne 1 -or
       @($trusted.controls|Where-Object relativePath -eq 'CLAUDE.md'|Where-Object active -eq $true).Count -ne 1){throw ('Trusted control files remained contained: '+($trusted.controls|ConvertTo-Json -Depth 8 -Compress))}
    if(-not $trusted.mcpRuntime.valid -or [int]$trusted.mcpRuntime.serverCount -ne 1){throw 'Trusted MCP runtime was not visible before send'}
    if(($trusted|ConvertTo-Json -Depth 18 -Compress).Contains('fixture-secret-must-not-leak')){throw 'Extensions endpoint leaked MCP environment value'}
    $request.sessionId=[guid]::NewGuid().ToString();$accepted=Api $connection '/api/chat/start' 'POST' $request
    $script:stage='Trusted task completion';$poll=Wait-Until {Api $connection ("/api/chat/poll/$($accepted.jobId)?after=0")} {param($value) $null -ne $value -and $value.status.state -eq 'completed'}
    $runRequestPath=Join-Path $root ".claude-gui-v2\runs\$($accepted.jobId)\request.json"
    $runRequest=Get-Content -LiteralPath $runRequestPath -Raw -Encoding UTF8|ConvertFrom-Json
    if($runRequest.claudeCodeIsolation.mode -ne 'bare' -or -not $runRequest.permissionMcpConfig){throw 'Accepted run did not persist native bare isolation evidence'}
    if(@($runRequest.mcpConfigs).Count -lt 2 -or @($runRequest.trustedMcpConfigs).Count -ne 1){throw ("Trusted MCP source was not routed explicitly: mcp={0}, trusted={1}, config={2}" -f @($runRequest.mcpConfigs).Count,@($runRequest.trustedMcpConfigs).Count,$runRequest.permissionMcpConfig)}
    if(-not (Test-Path -LiteralPath $runRequest.trustedInstructionPath)){throw 'Trusted project instructions were not materialized'}
    if($runRequest.mcpRuntime.serverCount -ne 1 -or $runRequest.mcpRuntime.servers[0].transport -ne 'stdio' -or
       [int]$runRequest.mcpRuntime.servers[0].environmentKeyCount -ne 1){throw 'MCP runtime capability manifest was incomplete'}
    $requestJson=$runRequest|ConvertTo-Json -Depth 18 -Compress
    $manifestPath=Join-Path $root ".claude-gui-v2\runs\$($accepted.jobId)\mcp-runtime.json"
    $manifestJson=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    if($requestJson.Contains('fixture-secret-must-not-leak') -or $manifestJson.Contains('fixture-secret-must-not-leak')){throw 'MCP environment value leaked into run evidence'}
    $runCountBefore=@(Get-ChildItem -LiteralPath (Join-Path $root '.claude-gui-v2\runs') -Directory).Count
    [IO.File]::WriteAllText($mcpFile,'{"mcpServers":{"broken":{}}}',[Text.UTF8Encoding]::new($false))
    Api $connection '/api/workbench/extensions/trust' 'POST' @{workspace=$root;type='control';name='.mcp.json';trusted=$true}|Out-Null
    $invalidExtensions=Api $connection ('/api/workbench/extensions?workspace='+[uri]::EscapeDataString($root))
    if($invalidExtensions.mcpRuntime.valid -ne $false){throw 'Invalid trusted MCP config was not visible in the extensions panel contract'}
    $request.sessionId=[guid]::NewGuid().ToString();$invalidMcp=Post-Raw $connection '/api/chat/start' $request
    if($invalidMcp.Status -ne 400 -or $invalidMcp.Body.code -ne 'invalid_mcp_runtime_config'){throw 'Malformed trusted MCP config was not blocked before Worker start'}
    $runCountAfter=@(Get-ChildItem -LiteralPath (Join-Path $root '.claude-gui-v2\runs') -Directory).Count
    if($runCountAfter -ne $runCountBefore){throw 'Rejected MCP preflight created a run directory'}
    [IO.File]::AppendAllText($skillFile,"modified-after-trust`n",[Text.UTF8Encoding]::new($false))
    $modified=Api $connection ('/api/workbench/extensions?workspace='+[uri]::EscapeDataString($root))
    $changed=@($modified.skills|Where-Object name -eq 'repo-review')[0]
    if(-not $modified.trust.blocked -or $changed.trustState -ne 'modified' -or $changed.active){throw 'Modified project Skill did not lose trust'}
    $request.sessionId=[guid]::NewGuid().ToString();$blockedAgain=Post-Raw $connection '/api/chat/start' $request
    if($blockedAgain.Status -ne 409){throw 'Modified project extension was not blocked before Worker start'}
    $jsonEvidence=$modified|ConvertTo-Json -Depth 14 -Compress
    if($jsonEvidence.Contains('stub-trust-secret')){throw 'Extension trust endpoint leaked Provider token'}
    [pscustomobject]@{ExtensionTrust='PASS';UnmanagedQuarantined=2;BackendBlocked=409;TrustedRun='completed';McpRuntime='validated-redacted';InvalidMcpBlocked=400;ContentFingerprint='OK';ModifiedRequarantined=$true;TokenLeaked=$false;Workspace=$root}|Format-List
}
finally {
    if($hostProcess){$candidate=Get-CimInstance Win32_Process -Filter "ProcessId=$($hostProcess.Id)" -ErrorAction SilentlyContinue;if($null-ne$candidate-and[string]::Equals([string]$candidate.ExecutablePath,$Executable,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}}
    Get-CimInstance Win32_Process|Where-Object{[string]::Equals([string]$_.ExecutablePath,$fake,[StringComparison]::OrdinalIgnoreCase)}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

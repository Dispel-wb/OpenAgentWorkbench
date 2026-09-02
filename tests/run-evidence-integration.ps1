param([Parameter(Mandatory=$true)][string]$Executable)

$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('agent-run-evidence-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$chinesePrompt = -join @([char]0x9A8C,[char]0x8BC1,[char]0x4E2D,[char]0x6587)
$attachment = Join-Path $testRoot 'evidence-utf8.txt'
[IO.File]::WriteAllText($attachment, ($chinesePrompt + ' Run Evidence offline integration.'), [Text.UTF8Encoding]::new($false))
$process = $null
$jobId = ''
$env:CLAUDE_GUI_WORKSPACE = $testRoot
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'run-evidence-' + [guid]::NewGuid().ToString('N')
try {
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $testRoot '.claude-gui-v2\runtime-state.json'
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 150
        $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Evidence test Host did not start' }

    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$runtime.authProtected), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers = @{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}
    $base = "http://127.0.0.1:$($runtime.port)"
    $provider = @{
        id='evidence-offline';name='Evidence Offline';token='fixture-token-not-a-secret';authStyle='bearer'
        text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('fixture-text')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | ConvertTo-Json -Depth 8
    Invoke-RestMethod -Method Post -Uri "$base/api/providers" -Headers $headers -ContentType 'application/json' -Body $provider | Out-Null

    $request = @{
        workspace=$testRoot;prompt=($chinesePrompt + ' Context evidence');attachments=@($attachment);allowedDirs=@($testRoot)
        sessionId='evidence-session';claudeSessionId='evidence-session';resume=$false
        providerId='evidence-offline';model='fixture-text';effort='high';permissionMode='readonly'
        allowedTools=@();disallowedTools=@()
    } | ConvertTo-Json -Depth 8
    $started = Invoke-RestMethod -Method Post -Uri "$base/api/chat/start" -Headers $headers -ContentType 'application/json' -Body $request
    $jobId = [string]$started.jobId
    if ([string]::IsNullOrWhiteSpace($jobId)) { throw 'Chat start did not return a run ID' }

    $evidence = $null
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        $evidence = Invoke-RestMethod -Uri "$base/api/chat/evidence/$jobId" -Headers $headers
        if (@($evidence.context.sources).Count -ge 4 -and @($evidence.artifacts).Count -ge 1) { break }
        Start-Sleep -Milliseconds 100
    }
    $types = @($evidence.context.sources | ForEach-Object { [string]$_.type })
    foreach ($required in @('user','permission','provider-route','attachment-reference')) {
        if ($types -notcontains $required) { throw "Missing context source: $required" }
    }
    if ([string]$evidence.runId -ne $jobId) { throw 'Evidence run ID mismatch' }
    if ([int]$evidence.summary.contextSources -lt 4 -or [int]$evidence.summary.artifacts -lt 1) { throw 'Evidence summary counts are incomplete' }
    if ([string]$evidence.artifacts[0].path -ne $attachment) { throw 'Attachment artifact path mismatch' }
    if (($evidence | ConvertTo-Json -Depth 12) -match 'fixture-token-not-a-secret') { throw 'Provider token leaked into run evidence' }

    [pscustomobject]@{
        RunEvidence='PASS';RunId=$jobId;ContextSources=[int]$evidence.summary.contextSources
        EstimatedTokens=[int]$evidence.summary.estimatedContextTokens;Artifacts=[int]$evidence.summary.artifacts
        Tools=[int]$evidence.summary.tools;TokenLeaked=$false;Chinese=$true
    } | Format-List
}
finally {
    if ($jobId -and $runtime) {
        try { Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($runtime.port)/api/chat/stop/$jobId" -Headers $headers -ContentType 'application/json' -Body '{}' | Out-Null } catch {}
    }
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $testRoot) -and $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

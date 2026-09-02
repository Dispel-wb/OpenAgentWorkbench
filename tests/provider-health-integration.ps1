param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for Provider health integration state'
}

function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until { try { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $null } } `
        { param($value) $null -ne $value -and $value.state -eq 'running' -and [int]$value.pid -ne $PreviousPid }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime = $runtime; Base = "http://127.0.0.1:$($runtime.port)"; Headers = @{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri = $Connection.Base + $Path; Headers = $Connection.Headers; Method = $Method }
    if ($null -ne $Body) { $request.ContentType = 'application/json; charset=utf-8'; $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 18 -Compress)) }
    Invoke-RestMethod @request
}

function Save-Provider($Connection, [string]$Id, [string]$Name, [string]$Model, [bool]$Vision) {
    Api $Connection '/api/providers' 'POST' @{
        id=$Id;name=$Name;token=('stub-' + $Id);authStyle='bearer'
        text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@($Model)}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
        capabilities=@{schemaVersion=1;evidencePolicy='fixture';models=@{$Model=@{vision=$Vision;contextWindow=128000;evidence='fixture'}}}
    } | Out-Null
}

function Run-Task($Connection, [string]$Root, [string]$ProviderId, [string]$Model, [string]$Prompt, [array]$Attachments = @()) {
    $session = [guid]::NewGuid().ToString()
    $job = Api $Connection '/api/chat/start' 'POST' @{
        workspace=$Root;prompt=$Prompt;attachments=$Attachments;allowedDirs=@();sessionId=$session;claudeSessionId=$session;resume=$false
        providerId=$ProviderId;model=$Model;effort='low';permissionMode='readonly';allowedTools=@();disallowedTools=@()
    }
    $poll = Wait-Until { Api $Connection ("/api/chat/poll/$($job.jobId)?after=0") } { param($value) $value.status.state -eq 'completed' -or $value.status.state -eq 'failed' }
    return @{ Job = $job; Poll = $poll }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-provider-health-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')
$image = Join-Path $root 'fixture.png'; [IO.File]::WriteAllBytes($image, [byte[]](137,80,78,71,13,10,26,10))

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_CLAUDE_EXE = $fake
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'provider-health-' + [guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null

try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    Save-Provider $connection 'current-provider' 'Current Provider' 'current-model' $true
    Save-Provider $connection 'healthy-provider' 'Healthy Provider' 'healthy-vision-model' $true
    Save-Provider $connection 'cooling-provider' 'Cooling Provider' 'cooling-model' $false
    Save-Provider $connection 'auth-provider' 'Auth Provider' 'auth-model' $false

    foreach ($unknownProbe in @(
        @{ path='/api/providers/discover'; body=@{providerId='missing-provider';baseUrl='http://127.0.0.1:9';authStyle='auto'} },
        @{ path='/api/providers/probe'; body=@{providerId='missing-provider';preset='siliconflow'} }
    )) {
        $missingStatus = 0
        try { Api $connection $unknownProbe.path 'POST' $unknownProbe.body | Out-Null }
        catch { $missingStatus = [int]$_.Exception.Response.StatusCode }
        if ($missingStatus -ne 404) { throw "Unknown Provider probe was not rejected with 404: $($unknownProbe.path) status=$missingStatus" }
    }
    $unknownHealth = @(Api $connection '/api/providers/health?providerId=missing-provider')
    if (@($unknownHealth | Where-Object { $_.providerId -eq 'missing-provider' }).Count -ne 0) { throw 'Unknown Provider polluted the health ledger' }

    $healthyRun = Run-Task $connection $root 'healthy-provider' 'healthy-vision-model' 'provider-success'
    if ($healthyRun.Poll.status.state -ne 'completed') { throw 'Healthy Provider fixture did not complete' }
    $coolingRun = Run-Task $connection $root 'cooling-provider' 'cooling-model' 'provider-rate-limit'
    if ($coolingRun.Poll.status.failureClassification.kind -ne 'rate_limit') { throw 'Rate limit failure was not classified' }
    $authRun = Run-Task $connection $root 'auth-provider' 'auth-model' 'provider-auth-failure'
    if ($authRun.Poll.status.failureClassification.kind -ne 'authentication') { throw 'Authentication failure was not classified' }

    $failed = Run-Task $connection $root 'current-provider' 'current-model' 'provider-rate-limit'
    $decision = $failed.Poll.status.fallbackDecision
    if ($decision.strategy -ne 'capability-health-ranked-user-confirmed' -or [bool]$decision.automatic) { throw 'Fallback strategy is not explicit user-confirmed health ranking' }
    if ($decision.classification.kind -ne 'rate_limit') { throw 'Fallback decision lost the failure classification' }
    if ($decision.candidates[0].providerId -ne 'healthy-provider' -or -not [bool]$decision.candidates[0].available) { throw 'Healthy Provider was not ranked first' }
    $coolingCandidate = @($decision.candidates | Where-Object providerId -eq 'cooling-provider')[0]
    if ($null -eq $coolingCandidate -or [bool]$coolingCandidate.available -or [long]$coolingCandidate.cooldownRemainingSeconds -le 0) { throw 'Cooling Provider was not visibly deprioritized' }
    if (@($decision.candidates[0].rankReasons).Count -lt 2) { throw 'Fallback ranking has no useful evidence reasons' }

    $visionFailure = Run-Task $connection $root 'current-provider' 'current-model' 'provider-rate-limit vision' @($image)
    $visionDecision = $visionFailure.Poll.status.fallbackDecision
    if (-not [bool]$visionDecision.requiresVision) { throw 'Visual requirement was not preserved in fallback decision' }
    if (@($visionDecision.candidates | Where-Object providerId -ne 'healthy-provider').Count -ne 0) { throw 'Non-vision Provider leaked into visual fallback candidates' }

    $health = Api $connection '/api/providers/health'
    $healthy = @($health | Where-Object { $_.providerId -eq 'healthy-provider' -and $_.model -eq 'healthy-vision-model' })[0]
    $cooling = @($health | Where-Object { $_.providerId -eq 'cooling-provider' -and $_.model -eq 'cooling-model' })[0]
    $auth = @($health | Where-Object { $_.providerId -eq 'auth-provider' -and $_.model -eq 'auth-model' })[0]
    if ($healthy.state -ne 'healthy' -or [long]$healthy.successCount -lt 1) { throw 'Healthy execution was not persisted' }
    if ($cooling.state -ne 'cooling' -or $cooling.failureKind -ne 'rate_limit') { throw 'Cooling execution was not persisted' }
    $healthJson = $health | ConvertTo-Json -Depth 18 -Compress
    if ($healthJson.Contains('sk-provider-health-secret-value') -or $healthJson.Contains('stub-')) { throw 'Provider health endpoint leaked a token' }
    if (-not $auth.lastError.Contains('[REDACTED]')) { throw 'Authentication error was not visibly redacted' }

    $oldPid = [int]$connection.Runtime.pid
    Stop-Process -Id $oldPid -Force; Start-Sleep -Milliseconds 350
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath $oldPid
    $restored = Api $connection '/api/providers/health'
    if (@($restored | Where-Object providerId -eq 'healthy-provider').Count -lt 1) { throw 'Provider health ledger did not survive Host restart' }

    [pscustomobject]@{
        ProviderHealth = 'PASS'; HealthyRankedFirst = $true; RateLimitClassified = $true; AuthenticationClassified = $true
        CoolingDeprioritized = $true; VisionFiltered = $true; UserConfirmationRequired = -not [bool]$decision.automatic
        HealthPersistedAfterRestart = $true; UnknownProviderBlocked = $true; HealthLedgerPollution = $false; TokenLeaked = $false; SchemaVersion = (Api $connection '/api/bootstrap').persistence.schemaVersion
        Workspace = $root
    } | Format-List
}
finally {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

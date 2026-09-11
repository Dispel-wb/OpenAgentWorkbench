param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [int]$Cycles = 100,
    [ValidateRange(0,5000)][int]$ObserverDelayMilliseconds = 0
)
$ErrorActionPreference = 'Stop'

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 35
        $value = & $Action
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for fault-injection state: $script:stressStage; state=$($value.state); runState=$($value.status.state); sent=$($value.sentOffset); completed=$($value.completedOffset)"
}

function Connect-Host([string]$RuntimePath, [int]$PreviousPid = 0) {
    $runtime = Wait-Until {
        try {
            if (Test-Path -LiteralPath $RuntimePath) {
                Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json
            }
        } catch { $null }
    } { param($v) $null -ne $v -and $v.state -eq 'running' -and [int]$v.pid -ne $PreviousPid }
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
        $request.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress))
    }
    Invoke-RestMethod @request
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-native-stress-' + [guid]::NewGuid().ToString('N'))
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
$env:CLAUDE_GUI_MUTEX_SCOPE = 'fault-stress-' + [guid]::NewGuid().ToString('N')
$faultGate = Join-Path $root 'fault-input.gate'
$env:CLAUDE_GUI_FAULT_GATE_FILE = $faultGate
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $script:stressStage = 'initial Host startup'
    $connection = Connect-Host $runtimePath
    Api $connection '/api/providers' 'POST' @{
        id='offline-stress';name='Offline Stress';token='stub-token';authStyle='bearer'
        text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')}
        image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}
    } | Out-Null

    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $sessionId = [guid]::NewGuid().ToString()
        $requestId = 'stress-request-' + $cycle
        $prompt = 'fault-recovery-' + $cycle
        # Keep the worker in flight until this test has observed the durable input and restarted the Host.
        [IO.File]::WriteAllText($faultGate, [string]$cycle)
        $job = Api $connection '/api/chat/start' 'POST' @{
            workspace=$root;prompt=$prompt;sessionId=$sessionId;claudeSessionId=$sessionId
            resume=$false;requestId=$requestId;providerId='offline-stress';model='offline-model'
            effort='low';permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()
        }
        $inputState = Join-Path $root ".claude-gui-v2\runs\$($job.jobId)\worker-input-state.json"
        $script:stressStage = "cycle $cycle in-flight input observation"
        if ($ObserverDelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $ObserverDelayMilliseconds }
        Wait-Until {
            if (Test-Path -LiteralPath $inputState) { Get-Content -LiteralPath $inputState -Raw -Encoding UTF8 | ConvertFrom-Json }
        } { param($v) $null -ne $v -and [long]$v.sentOffset -gt [long]$v.completedOffset } | Out-Null

        $oldPid = [int]$connection.Runtime.pid
        Stop-Process -Id $oldPid -Force
        $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
        $script:stressStage = "cycle $cycle Host restart"
        $connection = Connect-Host $runtimePath $oldPid
        [IO.File]::Delete($faultGate)
        $script:stressStage = "cycle $cycle terminal result"
        $poll = Wait-Until { Api $connection ("/api/chat/poll/$($job.jobId)?after=0") } { param($v) $v.status.state -eq 'completed' }
        $results = @($poll.events | Where-Object { $_.payload -match '"type":"result"' }).Count
        if ($results -ne 1) { throw "Cycle $cycle produced $results business results" }
        if (-not ((@($poll.events.payload) -join "`n").Contains($prompt))) { throw "Cycle $cycle lost acknowledged input" }
        if (($cycle % 10) -eq 0) { Write-Output "Passed $cycle / $Cycles fault cycles" }
    }

    $health = Api $connection '/api/bootstrap'
    if ($health.persistence.journalMode -ne 'wal' -or $health.persistence.integrity -ne 'ok') { throw 'SQLite integrity failed after stress run' }
    [pscustomobject]@{
        Cycles = $Cycles
        AcknowledgedEventLoss = 0
        DuplicateBusinessResults = 0
        AutoRecovery = 'OK'
        SQLiteIntegrity = 'OK'
        Workspace = $root
    } | Format-List
}
finally {
    Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -eq $Executable -or $_.ExecutablePath -eq $fake
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_FAULT_GATE_FILE -ErrorAction SilentlyContinue
}

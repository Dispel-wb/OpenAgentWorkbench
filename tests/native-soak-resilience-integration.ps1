param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$soakScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'native-soak.ps1')).Path
$fixtureScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'soak-resilience-fixture.js')).Path
$node = (Get-Command node -ErrorAction Stop).Source
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-soak-resilience-' + [guid]::NewGuid().ToString('N'))))
if (-not ($root.TrimEnd('\') + '\').StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe soak resilience root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$fixtures = [Collections.Generic.List[Diagnostics.Process]]::new()
$secretText = 'soak-resilience-secret-' + [guid]::NewGuid().ToString('N')

function Wait-File([string]$Path, [int]$Seconds = 10) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { if (Test-Path -LiteralPath $Path) { return }; Start-Sleep -Milliseconds 100 } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Path"
}

function Start-Fixture([string]$Name, [string]$Mode) {
    $caseRoot = Join-Path $root $Name
    $data = Join-Path $caseRoot '.claude-gui-v2'
    [IO.Directory]::CreateDirectory($data) | Out-Null
    $portFile = Join-Path $caseRoot 'port.txt'
    $process = Start-Process -FilePath $node -ArgumentList @($fixtureScript,$portFile,$Mode) -WorkingDirectory $caseRoot -WindowStyle Hidden -PassThru
    $fixtures.Add($process)
    Wait-File $portFile
    $port = [int]([IO.File]::ReadAllText($portFile).Trim())
    Add-Type -AssemblyName System.Security
    $protected = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($secretText), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $runtimePath = Join-Path $data 'runtime-state.json'
    $runtime = [ordered]@{state='running';pid=$process.Id;port=$port;appVersion='soak-fixture';authProtected=[Convert]::ToBase64String($protected);updatedAt=(Get-Date).ToString('o')}
    [IO.File]::WriteAllText($runtimePath, ($runtime | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{Root=$caseRoot;RuntimePath=$runtimePath;ProgressPath=(Join-Path $caseRoot 'progress.json');LogPath=(Join-Path $caseRoot 'events.jsonl');Process=$process}
}

try {
    $transient = Start-Fixture 'transient' 'transient'
    & $soakScript -Executable $Executable -Hours 0.0014 -SampleSeconds 1 -MaxRecoverySeconds 8 -SuspendGapSeconds 2 -AttachRuntime $transient.RuntimePath -ProgressPath $transient.ProgressPath -LogPath $transient.LogPath | Out-Null
    $transientProgress = Get-Content -LiteralPath $transient.ProgressPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $transientLog = Get-Content -LiteralPath $transient.LogPath -Raw -Encoding UTF8
    if ($transientProgress.state -ne 'passed' -or [int]$transientProgress.transientFailures -lt 1 -or [int]$transientProgress.requestRetries -lt 1) { throw ('Transient request failure was not recovered and recorded: ' + ($transientProgress | ConvertTo-Json -Depth 5 -Compress)) }
    if ([int]$transientProgress.suspendResumeCount -lt 1 -or $transientLog -notmatch 'suspend-resume-gap') { throw 'Suspend/resume scheduling gap was not recorded' }
    if ($transientLog -notmatch 'probe-retry' -or $transientLog -notmatch 'probe-recovered') { throw 'Probe retry lifecycle is missing from the detailed log' }
    if ($transientLog.Contains($secretText)) { throw 'Detailed soak log leaked the Host secret' }
    Stop-Process -Id $transient.Process.Id -Force -ErrorAction SilentlyContinue

    $permanent = Start-Fixture 'permanent' 'permanent-failure'
    $failedAsExpected = $false
    try {
        & $soakScript -Executable $Executable -Hours 0.001 -SampleSeconds 1 -MaxRecoverySeconds 5 -SuspendGapSeconds 2 -AttachRuntime $permanent.RuntimePath -ProgressPath $permanent.ProgressPath -LogPath $permanent.LogPath | Out-Null
    } catch { $failedAsExpected = $true }
    if (-not $failedAsExpected) { throw 'Permanent probe failure unexpectedly passed' }
    $failedProgress = Get-Content -LiteralPath $permanent.ProgressPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($failedProgress.state -ne 'failed' -or [int]$failedProgress.failures -ne 1) { throw 'Permanent failure did not write one terminal failure' }
    $evidence = [string]$failedProgress.lastFailureEvidencePath
    if (-not (Test-Path -LiteralPath $evidence -PathType Container)) { throw 'Failure evidence directory was not preserved' }
    foreach ($file in @('failure.json','runtime.sanitized.json','process.json','progress-before-failure.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $evidence $file) -PathType Leaf)) { throw "Failure evidence is missing $file" }
    }
    $evidenceText = (Get-ChildItem -LiteralPath $evidence -File | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    if ($evidenceText.Contains($secretText) -or $evidenceText -match 'authProtected') { throw 'Failure evidence leaked authentication material' }

    [pscustomobject]@{
        NativeSoakResilience = 'PASS'
        TransientFailures = [int]$transientProgress.transientFailures
        RequestRetries = [int]$transientProgress.requestRetries
        SuspendResumeCount = [int]$transientProgress.suspendResumeCount
        PermanentState = [string]$failedProgress.state
        EvidenceFiles = @(Get-ChildItem -LiteralPath $evidence -File).Count
        SecretLeaked = $false
    } | Format-List
}
finally {
    foreach ($fixture in $fixtures) { try { if (-not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue } } catch { } }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

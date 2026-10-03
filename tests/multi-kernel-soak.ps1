param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$NodePath,
    [Parameter(Mandatory=$true)][string]$ClaudePath,
    [Parameter(Mandatory=$true)][string]$CodexPath,
    [Parameter(Mandatory=$true)][string]$DshEntry,
    [Parameter(Mandatory=$true)][string]$PiEntry,
    [Parameter(Mandatory=$true)][string]$ProgressPath,
    [ValidateRange(0.01,168)][double]$Hours=72,
    [ValidateRange(30,3600)][int]$CycleIntervalSeconds=300,
    [ValidateRange(30,900)][int]$CycleTimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$NodePath=(Resolve-Path -LiteralPath $NodePath).Path
$ClaudePath=(Resolve-Path -LiteralPath $ClaudePath).Path
$CodexPath=(Resolve-Path -LiteralPath $CodexPath).Path
$DshEntry=(Resolve-Path -LiteralPath $DshEntry).Path
$PiEntry=(Resolve-Path -LiteralPath $PiEntry).Path
$ProgressPath=[IO.Path]::GetFullPath($ProgressPath)
$testsRoot=(Resolve-Path -LiteralPath $PSScriptRoot).Path
$pwsh=(Get-Command pwsh -ErrorAction Stop).Source
$candidateHash=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
$inputPaths=[ordered]@{candidate=$Executable;node=$NodePath;claude=$ClaudePath;codex=$CodexPath;dsh=$DshEntry;pi=$PiEntry;claudeTest=(Join-Path $testsRoot 'claude-permission-core-integration.ps1');codexTest=(Join-Path $testsRoot 'codex-core-offline-smoke.js');dshTest=(Join-Path $testsRoot 'dsh-core-offline-smoke.js');piTest=(Join-Path $testsRoot 'pi-host-integration.ps1');piFixture=(Join-Path $testsRoot 'pi-host-fixture.js');runner=(Join-Path $testsRoot 'multi-kernel-soak.ps1');launcher=(Join-Path $testsRoot 'start-multi-kernel-soak.ps1')}
$inputHashes=[ordered]@{
    candidate=$candidateHash
    node=(Get-FileHash -LiteralPath $NodePath -Algorithm SHA256).Hash
    claude=(Get-FileHash -LiteralPath $ClaudePath -Algorithm SHA256).Hash
    codex=(Get-FileHash -LiteralPath $CodexPath -Algorithm SHA256).Hash
    dsh=(Get-FileHash -LiteralPath $DshEntry -Algorithm SHA256).Hash
    pi=(Get-FileHash -LiteralPath $PiEntry -Algorithm SHA256).Hash
    claudeTest=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'claude-permission-core-integration.ps1') -Algorithm SHA256).Hash
    codexTest=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'codex-core-offline-smoke.js') -Algorithm SHA256).Hash
    dshTest=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'dsh-core-offline-smoke.js') -Algorithm SHA256).Hash
    piTest=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'pi-host-integration.ps1') -Algorithm SHA256).Hash
    piFixture=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'pi-host-fixture.js') -Algorithm SHA256).Hash
    runner=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'multi-kernel-soak.ps1') -Algorithm SHA256).Hash
    launcher=(Get-FileHash -LiteralPath (Join-Path $testsRoot 'start-multi-kernel-soak.ps1') -Algorithm SHA256).Hash
}
$started=[DateTimeOffset]::Now;$targetActiveSeconds=$Hours*3600;$deadline=$started.AddSeconds($targetActiveSeconds);$cycles=0;$activeSeconds=0.0;$lastObservation=$started;$suspendGaps=0
$passes=[ordered]@{claude=0;codex=0;dsh=0;pi=0};$failures=[ordered]@{claude=0;codex=0;dsh=0;pi=0};$recent=@();$state='running';$message=''
function Save-Progress {
    $now=[DateTimeOffset]::Now
    $data=[ordered]@{schemaVersion=1;state=$state;candidateSha256=$candidateHash;startedAt=$started.ToString('o');updatedAt=$now.ToString('o');deadline=$deadline.ToString('o');requestedHours=$Hours;activeDurationSeconds=[Math]::Floor($activeSeconds);cycles=$cycles;suspendGaps=$suspendGaps;parallelKernels=@('claude','codex','dsh','pi');passes=$passes;failures=$failures;totalFailures=($failures.claude+$failures.codex+$failures.dsh+$failures.pi);monitorPid=$PID;monitorStartedAt=[Diagnostics.Process]::GetCurrentProcess().StartTime.ToString('o');cycleIntervalSeconds=$CycleIntervalSeconds;inputPaths=$inputPaths;inputHashes=$inputHashes;recent=@($recent|Select-Object -Last 16);message=$message;agentWorkload=$(if($state-eq'passed'){'passed'}elseif($state-eq'failed'){'failed'}else{'running'})}
    [IO.Directory]::CreateDirectory((Split-Path $ProgressPath -Parent))|Out-Null
    $tmp=$ProgressPath+'.tmp';[IO.File]::WriteAllText($tmp,($data|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $ProgressPath){[IO.File]::Replace($tmp,$ProgressPath,($ProgressPath+'.previous'))}else{[IO.File]::Move($tmp,$ProgressPath)}
}
function Assert-Inputs {
    if((Get-FileHash $Executable -Algorithm SHA256).Hash-ne$inputHashes.candidate-or(Get-FileHash $NodePath -Algorithm SHA256).Hash-ne$inputHashes.node-or(Get-FileHash $ClaudePath -Algorithm SHA256).Hash-ne$inputHashes.claude-or(Get-FileHash $CodexPath -Algorithm SHA256).Hash-ne$inputHashes.codex-or(Get-FileHash $DshEntry -Algorithm SHA256).Hash-ne$inputHashes.dsh-or(Get-FileHash $PiEntry -Algorithm SHA256).Hash-ne$inputHashes.pi-or(Get-FileHash (Join-Path $testsRoot 'claude-permission-core-integration.ps1') -Algorithm SHA256).Hash-ne$inputHashes.claudeTest-or(Get-FileHash (Join-Path $testsRoot 'codex-core-offline-smoke.js') -Algorithm SHA256).Hash-ne$inputHashes.codexTest-or(Get-FileHash (Join-Path $testsRoot 'dsh-core-offline-smoke.js') -Algorithm SHA256).Hash-ne$inputHashes.dshTest-or(Get-FileHash (Join-Path $testsRoot 'pi-host-integration.ps1') -Algorithm SHA256).Hash-ne$inputHashes.piTest-or(Get-FileHash (Join-Path $testsRoot 'pi-host-fixture.js') -Algorithm SHA256).Hash-ne$inputHashes.piFixture-or(Get-FileHash (Join-Path $testsRoot 'multi-kernel-soak.ps1') -Algorithm SHA256).Hash-ne$inputHashes.runner-or(Get-FileHash (Join-Path $testsRoot 'start-multi-kernel-soak.ps1') -Algorithm SHA256).Hash-ne$inputHashes.launcher){throw 'Frozen binary, CLI, or test input changed during soak'}
}
function Observe-ActiveTime {
    $now=[DateTimeOffset]::Now
    $gap=($now-$script:lastObservation).TotalSeconds
    $script:lastObservation=$now
    $maximumExpectedGap=$CycleIntervalSeconds+$CycleTimeoutSeconds+120
    if($gap-gt$maximumExpectedGap){$script:suspendGaps++;$script:deadline=$script:deadline.AddSeconds($gap)}
    elseif($gap-gt0){$script:activeSeconds+=$gap}
}
function Quote([string]$Value){"'"+$Value.Replace("'","''")+"'"}
function Start-Kernel([string]$Name,[string]$Command){
    $stamp=(Get-Date).ToString('yyyyMMdd-HHmmss-fff');$out=Join-Path (Split-Path $ProgressPath -Parent) "$Name-$stamp.out.log";$err=Join-Path (Split-Path $ProgressPath -Parent) "$Name-$stamp.err.log"
    $p=Start-Process -FilePath $pwsh -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-Command',$Command) -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
    [pscustomobject]@{name=$Name;process=$p;out=$out;err=$err;started=[DateTimeOffset]::Now}
}
try {
    Save-Progress
    while($activeSeconds-lt$targetActiveSeconds){
        Observe-ActiveTime
        Assert-Inputs
        $claude="& $(Quote (Join-Path $testsRoot 'claude-permission-core-integration.ps1')) -Executable $(Quote $Executable) -Node $(Quote $NodePath)"
        $codex="& $(Quote $NodePath) $(Quote (Join-Path $testsRoot 'codex-core-offline-smoke.js')) $(Quote $Executable) $(Quote $CodexPath)"
        $dsh="& $(Quote $NodePath) $(Quote (Join-Path $testsRoot 'dsh-core-offline-smoke.js')) $(Quote $Executable) $(Quote $DshEntry)"
        $pi="& $(Quote (Join-Path $testsRoot 'pi-host-integration.ps1')) -Executable $(Quote $Executable) -PiEntry $(Quote $PiEntry) -NodePath $(Quote $NodePath) -CycleLimit 1 -CycleIntervalSeconds 1"
        $runs=@(Start-Kernel 'claude' $claude;Start-Kernel 'codex' $codex;Start-Kernel 'dsh' $dsh;Start-Kernel 'pi' $pi)
        $limit=(Get-Date).AddSeconds($CycleTimeoutSeconds)
        while(@($runs|Where-Object{-not$_.process.HasExited}).Count-and(Get-Date)-lt$limit){Start-Sleep -Milliseconds 500;foreach($run in $runs){$run.process.Refresh()}}
        foreach($run in $runs){
            if(-not$run.process.HasExited){Stop-Process -Id $run.process.Id -Force -ErrorAction SilentlyContinue;$failures[$run.name]++;throw "$($run.name) cycle timed out"}
            $ok=$run.process.ExitCode-eq0;if($ok){$passes[$run.name]++}else{$failures[$run.name]++}
            $stdout=if(Test-Path $run.out){[string](Get-Content $run.out -Raw -ErrorAction SilentlyContinue)}else{''};$stderr=if(Test-Path $run.err){[string](Get-Content $run.err -Raw -ErrorAction SilentlyContinue)}else{''}
            $outText=$(if($null-eq$stdout){''}else{$stdout.ToString().Trim()});$errText=$(if($null-eq$stderr){''}else{$stderr.ToString().Trim()})
            $recent+= [pscustomobject]@{kernel=$run.name;startedAt=$run.started.ToString('o');durationMs=[Math]::Round(([DateTimeOffset]::Now-$run.started).TotalMilliseconds);exitCode=$run.process.ExitCode;output=$outText;error=$errText}
            if(-not$ok){throw "$($run.name) cycle failed with exit code $($run.process.ExitCode)"}
        }
        $cycles++;Observe-ActiveTime;Save-Progress
        $remaining=$targetActiveSeconds-$activeSeconds;if($remaining-le0){break};Start-Sleep -Seconds ([Math]::Min($CycleIntervalSeconds,[Math]::Max(1,[int]$remaining)))
        Observe-ActiveTime;Save-Progress
    }
    Assert-Inputs;$state='passed';$message='All parallel kernel cycles completed with frozen inputs.';Save-Progress
}catch{$state='failed';$message=$_.Exception.Message;Save-Progress;throw}

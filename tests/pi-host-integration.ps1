param(
    [string]$Executable='dist/pi-candidate/OpenAgentWorkbench.exe',
    [string]$PiEntry='runtimes/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js',
    [string]$NodePath='',
    [switch]$KeepHost,
    [ValidateRange(0,168)][double]$SoakHours=0,
    [ValidateRange(1,1000)][int]$CycleLimit=1,
    [ValidateRange(1,3600)][int]$CycleIntervalSeconds=300,
    [ValidateRange(1,10000)][int]$MaxHandleGrowth=1500,
    [ValidateRange(0,999)][int]$ResourceWarmupCycles=0,
    [ValidateRange(1,10000)][int]$MaxPostWarmupHandleGrowth=10000,
    [switch]$RecordResourceTrace,
    [string]$ProgressPath=''
)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$PiEntry=(Resolve-Path -LiteralPath $PiEntry).Path
$node=if($NodePath){(Resolve-Path -LiteralPath $NodePath).Path}else{(Get-Command node -ErrorAction Stop).Source}
if($KeepHost-and$SoakHours-gt0){throw 'KeepHost is not allowed for a timed soak'}
if($SoakHours-gt0-and$CycleLimit-ne1){throw 'CycleLimit is only available for untimed resource checks'}
if($ResourceWarmupCycles-ge$CycleLimit-and$SoakHours-eq0){throw 'ResourceWarmupCycles must be lower than CycleLimit'}
if($ProgressPath-and(Test-Path -LiteralPath $ProgressPath)){throw 'Use a new progress path; do not overwrite an earlier soak'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('pi-host-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
if(-not$ProgressPath){$ProgressPath=Join-Path $root 'pi-soak-progress.json'}
$ProgressPath=[IO.Path]::GetFullPath($ProgressPath)
[IO.Directory]::CreateDirectory((Split-Path $ProgressPath -Parent))|Out-Null
$candidateHash=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
$entryHash=(Get-FileHash -LiteralPath $PiEntry -Algorithm SHA256).Hash
$nodeHash=(Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash
$soakStarted=[DateTimeOffset]::UtcNow
$lastObservation=$soakStarted
$activeSeconds=0.0;$cycles=0;$suspendGaps=0;$peakMemory=0L;$peakHandles=0;$currentMemory=0L;$currentHandles=0;$initialHandles=0;$postWarmupHandles=0;$postWarmupPeak=0
$resourceTrace=[Collections.Generic.List[object]]::new()
$mutexScope='pi-'+[guid]::NewGuid().ToString('N')
$fixture=$null;$hostProcess=$null;$jobId='';$jobIds=@();$hostBase='';$headers=@{}
function Post($route,$body){Invoke-RestMethod ($hostBase+$route) -Headers $headers -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 20 -Compress))) -TimeoutSec 20}
function Save-PiSoak([string]$State,[string]$Failure=''){
    $now=[DateTimeOffset]::UtcNow
    $progress=[ordered]@{
        schemaVersion=1;state=$State;candidateSha256=$candidateHash;piEntrySha256=$entryHash;nodeSha256=$nodeHash
        startedAt=$soakStarted.ToString('o');updatedAt=$now.ToString('o');completedAt=$(if($State-ne'running'){$now.ToString('o')}else{$null})
        requestedHours=$SoakHours;activeDurationSeconds=[Math]::Floor($activeSeconds);cycles=$cycles;turns=$cycles*2;cancellations=$cycles
        failures=$(if($State-eq'failed'){1}else{0});restarts=0;suspendGaps=$suspendGaps
        peakPrivateMemoryMB=[Math]::Round($peakMemory/1MB,1);peakHandles=$peakHandles;initialHandles=$initialHandles;handleGrowth=[Math]::Max(0,$peakHandles-$initialHandles)
        currentPrivateMemoryMB=[Math]::Round($currentMemory/1MB,1);currentHandles=$currentHandles
        resourceWarmupCycles=$ResourceWarmupCycles;postWarmupHandles=$postWarmupHandles;postWarmupPeakHandles=$postWarmupPeak;postWarmupHandleGrowth=$(if($postWarmupHandles-gt0){[Math]::Max(0,$postWarmupPeak-$postWarmupHandles)}else{0})
        resourceTrace=$(if($RecordResourceTrace){@($resourceTrace)}else{$null})
        monitorPid=$PID;monitorStartedAt=[Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().ToString('o');hostPid=$(if($hostProcess){$hostProcess.Id}else{0});workspace=$root;mutexScope=$mutexScope;message=$Failure
        workload='Same Host; real Pi readonly chat, two-turn resume, permission rejection and process-tree cancellation; local deterministic model, no paid provider'
    }
    $temp=$ProgressPath+'.tmp'
    [IO.File]::WriteAllText($temp,($progress|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $ProgressPath){[IO.File]::Replace($temp,$ProgressPath,($ProgressPath+'.previous'))}else{[IO.File]::Move($temp,$ProgressPath)}
}
function Observe-PiSoak {
    $now=[DateTimeOffset]::UtcNow
    $gap=($now-$script:lastObservation).TotalSeconds
    $script:lastObservation=$now
    # Do not count sleep/scheduler gaps toward active validation time.
    if($gap-gt30){$script:suspendGaps++}elseif($gap-gt0){$script:activeSeconds+=$gap}
    $hostProcess.Refresh()
    if($hostProcess.HasExited){throw 'Pi soak Host exited; automatic replacement is not certification'}
    $health=Invoke-RestMethod "$hostBase/api/workbench/health" -Headers $headers -TimeoutSec 10
    $bootstrap=Invoke-RestMethod "$hostBase/api/bootstrap" -Headers $headers -TimeoutSec 10
    if(-not$health.durableJobState-or$bootstrap.persistence.integrity-ne'ok'){throw 'Pi soak SQLite/health invariant failed'}
    $script:currentMemory=$hostProcess.PrivateMemorySize64
    $script:currentHandles=$hostProcess.HandleCount
    $script:peakMemory=[Math]::Max($peakMemory,$currentMemory)
    $script:peakHandles=[Math]::Max($peakHandles,$currentHandles)
    if($RecordResourceTrace){$resourceTrace.Add([pscustomobject]@{cycle=$cycles;activeSeconds=[Math]::Floor($activeSeconds);privateMemoryMB=[Math]::Round($currentMemory/1MB,1);handles=$currentHandles})}
    if($currentMemory-gt900MB-or($currentHandles-$initialHandles)-gt$MaxHandleGrowth){throw 'Pi soak exceeded memory/handle bounds'}
    if($ResourceWarmupCycles-gt0-and$cycles-eq$ResourceWarmupCycles-and$postWarmupHandles-eq0){$script:postWarmupHandles=$hostProcess.HandleCount;$script:postWarmupPeak=$hostProcess.HandleCount}
    elseif($postWarmupHandles-gt0-and$cycles-gt$ResourceWarmupCycles){$script:postWarmupPeak=[Math]::Max($postWarmupPeak,$hostProcess.HandleCount);if(($postWarmupPeak-$postWarmupHandles)-gt$MaxPostWarmupHandleGrowth){throw 'Pi Host handles did not plateau after warmup'}}
    Save-PiSoak 'running'
}
function Assert-PiSoakArtifacts {
    if((Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash-ne$candidateHash-or(Get-FileHash -LiteralPath $PiEntry -Algorithm SHA256).Hash-ne$entryHash-or(Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash-ne$nodeHash){throw 'Candidate, Pi entry or Node changed during validation'}
}
try {
    $portFile=Join-Path $root 'fixture-port.txt'
    $fixture=Start-Process $node -ArgumentList @(('"'+(Join-Path $PSScriptRoot 'pi-host-fixture.js')+'"'),('"'+$portFile+'"')) -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(10)
    while(-not(Test-Path -LiteralPath $portFile)-and(Get-Date)-lt$deadline){Start-Sleep -Milliseconds 100}
    $port=[int](Get-Content -LiteralPath $portFile -Raw)
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=[IO.Path]::GetFullPath($Executable);$start.Arguments='--host';$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.Environment['CLAUDE_GUI_WORKSPACE']=$root
    $start.Environment['CLAUDE_GUI_ROOT']=Join-Path $root 'install'
    $start.Environment['CLAUDE_GUI_TEST_MODE']='1'
    $start.Environment['CLAUDE_GUI_MUTEX_SCOPE']=$mutexScope
    $start.Environment['CLAUDE_GUI_PI_ENTRY']=[IO.Path]::GetFullPath($PiEntry)
    $start.Environment['CLAUDE_GUI_NODE_EXE']=$node
    $hostProcess=[Diagnostics.Process]::Start($start)
    $runtimePath=Join-Path $root '.claude-gui-v2/runtime-state.json'
    $deadline=(Get-Date).AddSeconds(20)
    do {Start-Sleep -Milliseconds 100;$runtime=if(Test-Path -LiteralPath $runtimePath){Get-Content -LiteralPath $runtimePath -Raw|ConvertFrom-Json}else{$null}} while(($null-eq$runtime-or$runtime.state-ne'running')-and(Get-Date)-lt$deadline)
    if($null-eq$runtime-or$runtime.state-ne'running'){throw 'Pi test Host failed to start'}
    Add-Type -AssemblyName System.Security
    $secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'};$hostBase="http://127.0.0.1:$($runtime.port)"
    $protected=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('pi-host-local-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $provider=@{id='pi-fixture';name='Pi local fixture';tokenEncrypted=$protected;authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl="http://127.0.0.1:$port/v1";models=@('fixture-model')};image=@{enabled=$false;models=@()}}
    $provider.token='pi-host-local-fixture';$provider.image.protocol='openai-images'
    Post '/api/providers' $provider|Out-Null
    Post '/api/settings' @{workspace=$root;workerHarness='pi';permissionMode='readonly';providerId='pi-fixture';model='fixture-model'}|Out-Null
    $initialHandles=$hostProcess.HandleCount
    $currentHandles=$initialHandles
    $currentMemory=$hostProcess.PrivateMemorySize64
    $lastObservation=[DateTimeOffset]::UtcNow
    Save-PiSoak 'running'
    do {
    $cycleStarted=[DateTimeOffset]::UtcNow
    Assert-PiSoakArtifacts
    $jobIds=@()
    $denials=0
    foreach($mode in @('manual',' Manual ')){
        $rejected=$false
        try{Post '/api/chat/start' @{workerHarness='pi';permissionMode=$mode;prompt='must reject'}|Out-Null}catch{$rejected=$_.ErrorDetails.Message-match'worker_capability_unsupported'}
        if(-not$rejected){throw "Pi accepted unsupported permission: $mode"};$denials++
    }
    foreach($extra in @(@{attachments=@(@{name='fixture.png';type='image'})})){
        $body=@{workerHarness='pi';permissionMode='readonly';prompt='must reject'};foreach($key in $extra.Keys){$body[$key]=$extra[$key]}
        $rejected=$false;try{Post '/api/chat/start' $body|Out-Null}catch{$rejected=$_.ErrorDetails.Message-match'worker_capability_unsupported'}
        if(-not$rejected){throw 'Pi ignored an unsupported attachment/tool restriction'};$denials++
    }
    $session=[guid]::NewGuid().ToString()
    for($turn=0;$turn-lt 2;$turn++){
        $chat=Post '/api/chat/start' @{workspace=$root;workerHarness='pi';permissionMode='readonly';providerId='pi-fixture';model='fixture-model';prompt=('PI_HOST_MARKER 中文 '+$turn);sessionId=$session;resume=($turn-gt 0)}
        $jobId=$chat.jobId;if($jobIds-notcontains$jobId){$jobIds+=$jobId};$deadline=(Get-Date).AddSeconds(20)
        do {Start-Sleep -Milliseconds 150;$poll=Invoke-RestMethod "$hostBase/api/chat/poll/$($jobId)?after=$($chat.eventCursor)" -Headers $headers;$terminal=@($poll.lines|ForEach-Object{$_|ConvertFrom-Json}|Where-Object type -eq result)}while(($terminal.Count-eq 0-or$poll.status.state-notin@('completed','failed','cancelled'))-and(Get-Date)-lt$deadline)
        if($terminal.Count-eq 0-or$terminal[-1].is_error-or$poll.status.state-ne'completed'){throw ('Pi Host round failed: '+($poll|ConvertTo-Json -Depth 20 -Compress))}
        $stored=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2/runs/$jobId/request.json") -Raw|ConvertFrom-Json
        if($stored.permissionBrokerEnabled-or$stored.coreIsolation.enforcedBy-ne'pi-workspace-policy-extension'){throw 'Pi permission metadata does not match enforcement'}
    }
    $apiRequest=Get-Content -LiteralPath ($portFile+'.request.json') -Raw|ConvertFrom-Json
    if((@($apiRequest.tools.function.name|Sort-Object)-join ',')-ne'find,grep,ls,read'){throw 'Readonly Pi tool allowlist is incorrect'}
    if(@($apiRequest.messages|Where-Object role -eq user).Count-lt 2){throw 'Pi Host lost conversation history'}
    $chat=Post '/api/chat/start' @{workspace=$root;workerHarness='pi';permissionMode='readonly';providerId='pi-fixture';model='fixture-model';prompt='PI_HOST_HANG';sessionId=[guid]::NewGuid().ToString()}
    $jobId=$chat.jobId;$jobIds+=$jobId;$deadline=(Get-Date).AddSeconds(15)
    do{Start-Sleep -Milliseconds 150;$last=Get-Content -LiteralPath ($portFile+'.request.json') -Raw}while($last-notmatch'PI_HOST_HANG'-and(Get-Date)-lt$deadline)
    if($last-notmatch'PI_HOST_HANG'){throw 'Pi hanging fixture never reached API'}
    $bridge=$null;$children=@();$deadline=(Get-Date).AddSeconds(10)
    do {
        $processes=@(Get-CimInstance Win32_Process)
        $bridge=$processes|Where-Object{$_.CommandLine-match'--agent-worker-bridge'-and$_.CommandLine-like('*'+$jobId+'*')}|Select-Object -First 1
        if($bridge){$children=@($processes|Where-Object ParentProcessId -eq $bridge.ProcessId)}
        if(-not$bridge-or$children.Count-eq 0){Start-Sleep -Milliseconds 100}
    } while((-not$bridge-or$children.Count-eq 0)-and(Get-Date)-lt$deadline)
    if(-not$bridge){throw 'Pi bridge process not found before cancellation'}
    if($children.Count-eq 0){throw 'Pi core process not found before cancellation'}
    Post "/api/chat/stop/$jobId" @{}|Out-Null
    Start-Sleep -Milliseconds 700
    foreach($item in @($bridge)+$children){if(Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue){throw "Stop left Pi process alive: $($item.ProcessId)"}}
    $cycles++
    Observe-PiSoak
    if($SoakHours-gt0){
        while($activeSeconds-lt$SoakHours*3600-and([DateTimeOffset]::UtcNow-$cycleStarted).TotalSeconds-lt$CycleIntervalSeconds){
            Start-Sleep -Milliseconds ([int]([Math]::Min(10,[Math]::Max(0.1,$SoakHours*3600-$activeSeconds))*1000))
            Observe-PiSoak
        }
    }
    } while(($SoakHours-gt0-and$activeSeconds-lt$SoakHours*3600)-or($SoakHours-eq0-and$cycles-lt$CycleLimit))
    Assert-PiSoakArtifacts
    Save-PiSoak 'passed'
    [pscustomobject]@{PiHost='PASS';Turns=$cycles*2;Cycles=$cycles;RequestedHours=$SoakHours;RequestedCycles=$CycleLimit;ActiveSeconds=[Math]::Floor($activeSeconds);PermissionDenials=$denials;NativeResume=$true;ProcessTreeStop=$true;InitialHandles=$initialHandles;PeakHandles=$peakHandles;CurrentHandles=$currentHandles;HandleGrowth=[Math]::Max(0,$peakHandles-$initialHandles);PostWarmupHandles=$postWarmupHandles;PostWarmupPeakHandles=$postWarmupPeak;PostWarmupHandleGrowth=$(if($postWarmupHandles-gt0){[Math]::Max(0,$postWarmupPeak-$postWarmupHandles)}else{0});Root=$root;HostBase=$hostBase;ProgressPath=$ProgressPath}|Format-List
    if($KeepHost){$hostProcess=$null;$fixture=$null}
} catch {
    $failure=$_
    try { Save-PiSoak 'failed' $failure.Exception.Message } catch { }
    throw $failure
} finally {
    if($hostProcess-and-not$hostProcess.HasExited){foreach($id in $jobIds){try{Post "/api/chat/stop/$id" @{}|Out-Null}catch{}};$hostProcess.Kill();$hostProcess.WaitForExit(5000)|Out-Null}
    if($fixture-and-not$fixture.HasExited){$fixture.Kill();$fixture.WaitForExit(3000)|Out-Null}
}

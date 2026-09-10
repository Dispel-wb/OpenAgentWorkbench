param([string]$Executable='dist/pi-candidate/OpenAgentWorkbench.exe',[string]$PiEntry='runtimes/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js',[switch]$KeepHost)
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('pi-host-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$fixture=$null;$hostProcess=$null;$jobId='';$jobIds=@();$hostBase='';$headers=@{}
function Post($route,$body){Invoke-RestMethod ($hostBase+$route) -Headers $headers -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 20 -Compress))) -TimeoutSec 20}
try {
    $node=(Get-Command node).Source
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
    $start.Environment['CLAUDE_GUI_MUTEX_SCOPE']='pi-'+[guid]::NewGuid().ToString('N')
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
    $denials=0
    foreach($mode in @('agent','edit','manual','scoped',' Manual ')){
        $rejected=$false
        try{Post '/api/chat/start' @{workerHarness='pi';permissionMode=$mode;prompt='must reject'}|Out-Null}catch{$rejected=$_.ErrorDetails.Message-match'worker_capability_unsupported'}
        if(-not$rejected){throw "Pi accepted unsupported permission: $mode"};$denials++
    }
    foreach($extra in @(@{attachments=@(@{name='fixture.png';type='image'})},@{disallowedTools=@('Bash')})){
        $body=@{workerHarness='pi';permissionMode='readonly';prompt='must reject'};foreach($key in $extra.Keys){$body[$key]=$extra[$key]}
        $rejected=$false;try{Post '/api/chat/start' $body|Out-Null}catch{$rejected=$_.ErrorDetails.Message-match'worker_capability_unsupported'}
        if(-not$rejected){throw 'Pi ignored an unsupported attachment/tool restriction'};$denials++
    }
    $session=[guid]::NewGuid().ToString()
    for($turn=0;$turn-lt 2;$turn++){
        $chat=Post '/api/chat/start' @{workspace=$root;workerHarness='pi';permissionMode='readonly';providerId='pi-fixture';model='fixture-model';prompt=('PI_HOST_MARKER 中文 '+$turn);sessionId=$session;resume=($turn-gt 0)}
        $jobId=$chat.jobId;if($jobIds-notcontains$jobId){$jobIds+=$jobId};$deadline=(Get-Date).AddSeconds(20)
        do {Start-Sleep -Milliseconds 150;$poll=Invoke-RestMethod "$hostBase/api/chat/poll/$($jobId)?after=$($chat.eventCursor)" -Headers $headers;$terminal=@($poll.lines|ForEach-Object{$_|ConvertFrom-Json}|Where-Object type -eq result)}while($terminal.Count-eq 0-and(Get-Date)-lt$deadline)
        if($terminal.Count-eq 0-or$terminal[-1].is_error){throw ('Pi Host round failed: '+($poll|ConvertTo-Json -Depth 20 -Compress))}
        $stored=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2/runs/$jobId/request.json") -Raw|ConvertFrom-Json
        if($stored.permissionBrokerEnabled-or$stored.coreIsolation.enforcedBy-ne'pi-tools-disabled'){throw 'Pi permission metadata does not match enforcement'}
    }
    $apiRequest=Get-Content -LiteralPath ($portFile+'.request.json') -Raw|ConvertFrom-Json
    if($apiRequest.tools.Count-gt 0){throw 'Readonly Pi exposed native tools'}
    if(@($apiRequest.messages|Where-Object role -eq user).Count-lt 2){throw 'Pi Host lost conversation history'}
    $chat=Post '/api/chat/start' @{workspace=$root;workerHarness='pi';permissionMode='readonly';providerId='pi-fixture';model='fixture-model';prompt='PI_HOST_HANG';sessionId=[guid]::NewGuid().ToString()}
    $jobId=$chat.jobId;$jobIds+=$jobId;$deadline=(Get-Date).AddSeconds(15)
    do{Start-Sleep -Milliseconds 150;$last=Get-Content -LiteralPath ($portFile+'.request.json') -Raw}while($last-notmatch'PI_HOST_HANG'-and(Get-Date)-lt$deadline)
    if($last-notmatch'PI_HOST_HANG'){throw 'Pi hanging fixture never reached API'}
    $processes=@(Get-CimInstance Win32_Process)
    $bridge=$processes|Where-Object{$_.CommandLine-match'--agent-worker-bridge'-and$_.CommandLine-like('*'+$jobId+'*')}|Select-Object -First 1
    if(-not$bridge){throw 'Pi bridge process not found before cancellation'}
    $children=@($processes|Where-Object ParentProcessId -eq $bridge.ProcessId)
    if($children.Count-eq 0){throw 'Pi core process not found before cancellation'}
    Post "/api/chat/stop/$jobId" @{}|Out-Null
    Start-Sleep -Milliseconds 700
    foreach($item in @($bridge)+$children){if(Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue){throw "Stop left Pi process alive: $($item.ProcessId)"}}
    [pscustomobject]@{PiHost='PASS';Turns=2;PermissionDenials=$denials;NativeResume=$true;ProcessTreeStop=$true;Root=$root;HostBase=$hostBase}|Format-List
    if($KeepHost){$hostProcess=$null;$fixture=$null}
} finally {
    if($hostProcess-and-not$hostProcess.HasExited){foreach($id in $jobIds){try{Post "/api/chat/stop/$id" @{}|Out-Null}catch{}};$hostProcess.Kill();$hostProcess.WaitForExit(5000)|Out-Null}
    if($fixture-and-not$fixture.HasExited){$fixture.Kill();$fixture.WaitForExit(3000)|Out-Null}
}

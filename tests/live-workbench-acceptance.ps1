param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [switch]$AllowPaid,
    [string]$SourceData='D:\work\Claude\.claude-gui-v2',
    [string]$ProviderId='', [string]$Model='',
    [switch]$VerifyBoundary,
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '..\dist\live-acceptance'),
    [ValidateSet('files','dag','all')][string]$Scenario='all'
)
$ErrorActionPreference='Stop'
if(-not $AllowPaid){throw 'Explicit -AllowPaid is required. This test makes real billable requests.'}
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot=Join-Path $temporaryRoot ('workbench-paid-'+[guid]::NewGuid().ToString('N'))
$workspace=Join-Path $fixtureRoot '中文 工作区'
$data=Join-Path $workspace '.claude-gui-v2'
[IO.Directory]::CreateDirectory($data)|Out-Null
$process=$null; $connection=$null; $activeRuns=[Collections.Generic.HashSet[string]]::new()
$report=[ordered]@{schemaVersion=1;startedAt=[DateTimeOffset]::Now.ToString('o');sha256=(Get-FileHash $Executable).Hash;scenario=$Scenario;state='running';steps=@();formalDataModified=$false;paid=$true}
$reportPath=Join-Path $OutputDirectory ('acceptance-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6)+'.json')
function Redact([string]$Text){return ($Text -replace 'sk-[A-Za-z0-9_-]+','[REDACTED]' -replace '(?i)Bearer\s+[^\s"<>]+','Bearer [REDACTED]')}
function Save-Report {
    [IO.File]::WriteAllText($reportPath,($report|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
}
function Api([string]$Path,[string]$Method='GET',$Body=$null,[int]$Timeout=20){
    $options=@{Uri=$connection.Base+$Path;Headers=$connection.Headers;Method=$Method;TimeoutSec=$Timeout}
    if($null-ne$Body){$options.ContentType='application/json; charset=utf-8';$options.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 18 -Compress))}
    try {Invoke-RestMethod @options} catch {throw (Redact ($_.Exception.Message+' '+$_.ErrorDetails.Message))}
}
function Wait-Result($Started,[string]$Name){
    $job=[string]$Started.jobId; $activeRuns.Add($job)|Out-Null
    $cursor=[long]($Started.eventCursor ?? 0);$timer=[Diagnostics.Stopwatch]::StartNew()
    $events=@();$result=$null;$state='';$lastPoll=$null
    do {
        Start-Sleep -Milliseconds 300
        $lastPoll=Api "/api/chat/poll/$job`?after=$cursor"
        foreach($entry in $lastPoll.events){
            $cursor=[Math]::Max($cursor,[long]$entry.seq)
            try{$value=$entry.payload|ConvertFrom-Json}catch{continue}
            $events+= $value
            if($value.type-eq'result'){$result=$value}
        }
        $cursor=[Math]::Max($cursor,[long]$lastPoll.nextSeq)
        $state=[string]($lastPoll.status.terminalState ?? $lastPoll.status.state)
    }while(($state-notin@('completed','failed','cancelled')-or ($state-eq'completed'-and$null-eq$result))-and$timer.Elapsed.TotalSeconds-lt 100)
    $tools=@($events|Where-Object type -eq assistant|ForEach-Object{$_.message.content}|Where-Object type -eq tool_use|ForEach-Object{$_.name}|Select-Object -Unique)
    $step=[ordered]@{name=$Name;runId=$job;state=$state;waitMs=$timer.ElapsedMilliseconds;coreDurationMs=$result.duration_ms;reused=[bool]$Started.reused;toolNames=$tools;usageReported=($null-ne$result.usage);inputTokens=$result.usage.input_tokens;outputTokens=$result.usage.output_tokens;cacheReadTokens=$result.usage.cache_read_input_tokens;cacheWriteTokens=$result.usage.cache_creation_input_tokens}
    $report.steps+= $step; Save-Report
    if($state-ne'completed'-or$null-eq$result-or[bool]$result.is_error){
        $step.error=Redact (($lastPoll.status.message+' '+$result.result+' '+($result.errors -join ' ')).Trim())
        $step.eventTypes=@($events|ForEach-Object{[string]$_.type+':'+[string]$_.subtype}|Select-Object -Unique)
        $init=@($events|Where-Object{$_.type-eq'system'-and$_.subtype-eq'init'})|Select-Object -First 1
        $step.coreTools=$init.tools; $step.mcp=$init.mcp_servers
        Save-Report
        throw "Acceptance failed at $Name ($state): $($step.error)"
    }
    return @{runId=$job;result=[string]$result.result;tools=$tools}
}
function Start-Turn([string]$Prompt,[string]$Session,[bool]$Resume=$false){
    Api '/api/chat/start' 'POST' @{workspace=$workspace;prompt=$Prompt;sessionId=$Session;claudeSessionId=$Session;resume=$Resume;requestId=('paid-'+[guid]::NewGuid().ToString('N'));workerHarness='claude';providerId=$ProviderId;model=$Model;effort='low';maxTurns=10;maxRetries=0;permissionMode='scoped';allowedTools=@('Read','Write','Edit');disallowedTools=@('Bash','WebFetch','WebSearch','Agent','Task','Skill');attachments=@();allowedDirs=@()}
}
try {
    Copy-Item -LiteralPath (Join-Path $SourceData 'providers-v2.json') -Destination (Join-Path $data 'providers-v2.json')
    Copy-Item -LiteralPath (Join-Path $SourceData 'settings.json') -Destination (Join-Path $data 'settings.json')
    $start=[Diagnostics.ProcessStartInfo]::new($Executable,'--host')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$workspace
    $start.Environment['CLAUDE_GUI_WORKSPACE']=$workspace
    $start.Environment['CLAUDE_GUI_ROOT']='D:\softwares\ClaudeCode'
    $start.Environment['CLAUDE_GUI_TEST_MODE']='1'
    $start.Environment['CLAUDE_GUI_MUTEX_SCOPE']='paid-'+[guid]::NewGuid().ToString('N')
    # Isolate core history and disable automatic SDK transport retries for paid acceptance.
    $start.Environment['CLAUDE_CONFIG_DIR']=Join-Path $fixtureRoot 'claude-config'
    $start.Environment['CLAUDE_CODE_MAX_OUTPUT_TOKENS']='1024'
    $start.Environment['CLAUDE_CODE_MAX_RETRIES']='0'
    $process=[Diagnostics.Process]::Start($start)
    $expires=(Get-Date).AddSeconds(20);$runtime=$null
    do {Start-Sleep -Milliseconds 100;try{$runtime=Get-Content (Join-Path $data 'runtime-state.json') -Raw -Encoding UTF8|ConvertFrom-Json}catch{}}while(($null-eq$runtime-or$runtime.state-ne'running')-and(Get-Date)-lt$expires)
    if($null-eq$runtime-or$runtime.state-ne'running'){throw 'Isolated Host failed to start'}
    Add-Type -AssemblyName System.Security
    $secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $connection=@{Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}}
    $bootstrap=Api '/api/bootstrap'
    if(-not $ProviderId){$ProviderId=[string]$bootstrap.settings.providerId}
    if(-not $Model){$Model=[string]$bootstrap.settings.model}
    $provider=@($bootstrap.providers|Where-Object id -eq $ProviderId)|Select-Object -First 1
    if(-not $provider-or $Model-notin $provider.text.models){throw 'Select a model already saved under this Provider'}
    $report.paid= -not ([uri]$provider.text.baseUrl).IsLoopback
    if($VerifyBoundary-and$report.paid){throw 'Deliberate boundary probes are offline-only'}
    $report.provider=$ProviderId;$report.model=$Model;$report.version=(Get-Item -LiteralPath $Executable).VersionInfo.FileVersion;Save-Report
    $session=[guid]::NewGuid().ToString()
    $marker='验收-'+[guid]::NewGuid().ToString('N').Substring(0,12)
    $inputFile=Join-Path $workspace '输入 数据.txt';$outputFile=Join-Path $workspace '输出 结果.txt'
    [IO.File]::WriteAllText($inputFile,$marker,[Text.UTF8Encoding]::new($false))
    if($Scenario-in@('files','all')){
        $first=Wait-Result (Start-Turn "验收任务，只用 Read 读取文件 '$inputFile'，再用当前可用的文件写入或编辑工具把读到的内容原样写到 '$outputFile'（新文件，Edit 可用空 old_string 创建）。不要读其他文件，不要调用其他工具，完成后只回复：文件处理完成。" $session) 'file-read-write'
        if(-not(Test-Path -LiteralPath $outputFile)-or [IO.File]::ReadAllText($outputFile).Trim()-ne$marker-or 'Read'-notin$first.tools-or (-not @($first.tools|Where-Object{$_-in@('Write','Edit')}).Count)){throw 'Actual file content/tool evidence mismatch'}
        $second=Wait-Result (Start-Turn '不要调用工具，不要读取文件。只回复上一轮文件里的完整内容。' $session $true) 'same-session-recall'
        if(-not $second.result.Contains($marker)){throw 'Second turn lost the previous tool result'}
        $parentOne=$first.runId
    }else{$parentOne=(Wait-Result (Start-Turn '仅回复：父任务甲就绪。不要调用工具。' $session) 'parent-one').runId}
    if($VerifyBoundary){
        $outside=Join-Path $fixtureRoot 'outside-must-not-exist.txt'
        $denial=Wait-Result (Start-Turn "只回复安全边界测试。permission-boundary: '$outside'。不得写工作区外文件。" ([guid]::NewGuid().ToString())) 'outside-write-denial'
        if((Test-Path -LiteralPath $outside)-or -not$denial.result.Contains('安全边界拒绝正常')){throw 'Scoped worker wrote outside its authorized workspace'}
    }
    if($Scenario-in@('dag','all')){
        $parentTwo=(Wait-Result (Start-Turn '仅回复：父任务乙就绪。不要调用工具。' ([guid]::NewGuid().ToString())) 'parent-two').runId
        $handoff='交接-'+[guid]::NewGuid().ToString('N').Substring(0,10)
        $workflow=Api '/api/workflows' 'POST' @{name='真实付费双父任务验收';maxParallel=1;nodes=@(@{id='produce';parentRunId=$parentOne;prompt="不要调用工具，只回复：$handoff"},@{id='consume';parentRunId=$parentTwo;prompt='不要调用工具，提取前置 Agent 输出中的交接标记，只回复该完整标记。';dependencies=@('produce');includeDependencyResults=$true})}
        $report.workflowId=$workflow.id;Save-Report
        $expires=(Get-Date).AddSeconds(150)
        do {
            Start-Sleep -Milliseconds 500
            $snapshot=$null; $list=Api '/api/workflows'
            foreach($item in $list){if($item.id-eq$workflow.id){$snapshot=$item;break}}
            if($null-eq$snapshot){throw 'Created DAG missing from workflow listing'}
        }while($snapshot.state-eq'running'-and(Get-Date)-lt$expires)
        if($snapshot.state-ne'completed'){Api '/api/workflows' 'POST' @{id=$workflow.id;action='cancel'}|Out-Null;throw ('DAG did not complete: '+(Redact ($snapshot|ConvertTo-Json -Depth 10 -Compress)))}
        foreach($node in $snapshot.nodes){$child=Wait-Result @{jobId=$node.runId} ('dag-'+$node.id);if(-not $child.result.Contains($handoff)){throw 'DAG dependency handoff was not preserved'}}
    }
    $report.state='passed'
} catch {$report.state='failed';$report.error=Redact $_.Exception.Message;throw}
finally {
    if($connection){
        if($report.workflowId-and$report.state-ne'passed'){try{Api '/api/workflows' 'POST' @{id=$report.workflowId;action='cancel'}|Out-Null}catch{}}
        foreach($run in $activeRuns){try{Api "/api/chat/stop/$run" 'POST' @{} 5|Out-Null}catch{}}
    }
    if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue;$process.WaitForExit(5000)|Out-Null}
    $report.finishedAt=[DateTimeOffset]::Now.ToString('o');Save-Report
    $resolved=[IO.Path]::GetFullPath($fixtureRoot)
    if($resolved.StartsWith($temporaryRoot,[StringComparison]::OrdinalIgnoreCase)-and [IO.Path]::GetFileName($resolved).StartsWith('workbench-paid-')){Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue}
    Write-Output "Workbench acceptance (paid=$($report.paid)): $($report.state); evidence: $reportPath"
}

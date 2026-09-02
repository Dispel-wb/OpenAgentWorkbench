param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$SourceData = 'D:\work\Claude\.claude-gui-v2'
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-live-agent-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe live Agent test root' }
$data = Join-Path $root '.claude-gui-v2'
[IO.Directory]::CreateDirectory($data) | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceData 'providers-v2.json') -Destination (Join-Path $data 'providers-v2.json')
Copy-Item -LiteralPath (Join-Path $SourceData 'settings.json') -Destination (Join-Path $data 'settings.json')
$process = $null
$connection = $null
$jobId = ''
function Api([string]$Path,[string]$Method='GET',$Body=$null,[int]$TimeoutSec=20) {
    $request=@{Uri=$connection.Base+$Path;Headers=$connection.Headers;Method=$Method;TimeoutSec=$TimeoutSec}
    if($null-ne$Body){$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress))}
    Invoke-RestMethod @request
}
try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'live-agent-' + [guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $data 'runtime-state.json'
    $deadline = (Get-Date).AddSeconds(20)
    do { Start-Sleep -Milliseconds 100;try{$runtime=if(Test-Path -LiteralPath $runtimePath){Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}}catch{$runtime=$null} } while (($null-eq$runtime-or$runtime.state-ne'running')-and(Get-Date)-lt$deadline)
    if($null-eq$runtime-or$runtime.state-ne'running'){throw'Live Agent Host did not start'}
    Add-Type -AssemblyName System.Security
    $secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $connection=@{Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}}
    $bootstrap=Api '/api/bootstrap';$providerId=[string]$bootstrap.settings.providerId;$model=[string]$bootstrap.settings.model
    $runtimeProbe=Api '/api/workbench/runtime/claude?probe=1' 'GET' $null 15
    if(-not[bool]$runtimeProbe.available-or-not[bool]$runtimeProbe.probeOk){throw('Claude Code runtime unavailable: '+[string]$runtimeProbe.probeError)}
    $sessionId=[guid]::NewGuid().ToString();$started=Api '/api/chat/start' 'POST' @{workspace=$root;prompt='仅回复：中文链路正常。不要调用任何工具。';attachments=@();allowedDirs=@();sessionId=$sessionId;claudeSessionId=$sessionId;resume=$false;requestId=('live-agent-'+[guid]::NewGuid().ToString('N'));providerId=$providerId;model=$model;effort='low';maxTurns=10;permissionMode='readonly';allowedTools=@();disallowedTools=@('Bash','Write','Edit','WebFetch','WebSearch') } 25
    $jobId=[string]$started.jobId;if([string]::IsNullOrWhiteSpace($jobId)){throw'Live Agent did not return a Run ID'}
    $deadline=(Get-Date).AddSeconds(75);$poll=$null
    do { Start-Sleep -Milliseconds 300;$poll=Api ("/api/chat/poll/$jobId`?after=0") 'GET' $null 15;$state=[string]($poll.status.terminalState ?? $poll.status.state) } while($state-notin@('completed','failed','cancelled')-and(Get-Date)-lt$deadline)
    if($state-notin@('completed','failed','cancelled')){Api ("/api/chat/stop/$jobId") 'POST' @{} 10|Out-Null;throw'Live Agent chain exceeded 75 seconds and was cancelled'}
    $eventPayloads=@($poll.events|ForEach-Object{[string]$_.payload});$resultEvent=$null
    foreach($payload in $eventPayloads){try{$parsed=$payload|ConvertFrom-Json;if($parsed.type-eq'result'){$resultEvent=$parsed}}catch{}}
    if($state-ne'completed'-or$null-eq$resultEvent-or[bool]$resultEvent.is_error){$errorText=[string]$poll.status.message;if(-not$errorText){$errorText='Agent Run did not produce a successful result'};throw($errorText-replace'sk-[A-Za-z0-9_-]+','[REDACTED]')}
    $text=[string]$resultEvent.result
    if(-not$text.Contains('中文链路正常')){throw('Live Agent returned unexpected text: '+$text.Substring(0,[Math]::Min(120,$text.Length)))}
    [pscustomobject]@{LiveAgentChain='PASS';Provider=$providerId;Model=$model;ClaudeRuntime=[string]$runtimeProbe.version;RunState=$state;ChineseRoundTrip=$true;Result=$text;InputTokens=[int]$resultEvent.usage.input_tokens;OutputTokens=[int]$resultEvent.usage.output_tokens;ToolCalls=0;SecretPrinted=$false;FormalDataModified=$false} | Format-List
}
finally {
    if($jobId-and$connection){try{Api ("/api/chat/stop/$jobId") 'POST' @{} 5|Out-Null}catch{}}
    if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if((Test-Path -LiteralPath $root)-and$root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
}

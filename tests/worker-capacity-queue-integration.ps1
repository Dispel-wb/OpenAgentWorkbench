param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop';$Executable=(Resolve-Path -LiteralPath $Executable).Path
function Wait-Until([scriptblock]$Action,[scriptblock]$Predicate,[int]$Seconds=25){$expires=(Get-Date).AddSeconds($Seconds);do{Start-Sleep -Milliseconds 100;try{$value=&$Action}catch{$value=$null};if(&$Predicate $value){return $value}}while((Get-Date)-lt$expires);throw('Timed out waiting for capacity queue: '+$script:stage)}
function Connect-Host([string]$RuntimePath){$runtime=Wait-Until{try{if(Test-Path -LiteralPath $RuntimePath){Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8|ConvertFrom-Json}}catch{$null}}{param($v)$null-ne$v-and$v.state-eq'running'};Add-Type -AssemblyName System.Security;$plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser);@{Runtime=$runtime;Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}}}
function Api($Connection,[string]$Path,[string]$Method='GET',$Body=$null){$args=@{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method};if($null-ne$Body){$args.ContentType='application/json; charset=utf-8';$args.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12 -Compress))};Invoke-RestMethod @args}
function Api-Status($Connection,[string]$Path,$Body){
 $bytes=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12 -Compress))
 $response=Invoke-WebRequest -UseBasicParsing -SkipHttpErrorCheck -Uri ($Connection.Base+$Path) -Headers $Connection.Headers -Method POST -ContentType 'application/json; charset=utf-8' -Body $bytes
 @{Status=[int]$response.StatusCode;Body=[string]$response.Content}
}

$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-capacity-'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $root 'fake-claude.exe';$vsRoot='C:\Program Files\Microsoft Visual Studio\2022\Community';$csc=Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe';$json=Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
&$csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs');if($LASTEXITCODE-ne0){throw'Fake worker build failed'};Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll');Add-Type -LiteralPath $json
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='capacity-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json';$hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
try{
 $connection=Connect-Host $runtimePath
 Api $connection '/api/providers' 'POST' @{id='offline-capacity';name='Offline Capacity';token='stub';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
 $runs=@();$sessions=@();$common=@{workspace=$root;resume=$false;providerId='offline-capacity';model='offline-model';effort='low';maxTurns=137;permissionMode='readonly';attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()}
 for($index=0;$index-lt4;$index++){$session=[guid]::NewGuid().ToString();$sessions+=$session;$body=$common.Clone();$body.prompt="run-center-slow-capacity-$index";$body.sessionId=$session;$body.claudeSessionId=$session;$runs+=Api $connection '/api/chat/start' 'POST' $body}
 $fifth=[guid]::NewGuid().ToString();$fifthBody=$common.Clone();$fifthBody.prompt='capacity-fifth';$fifthBody.sessionId=$fifth;$fifthBody.claudeSessionId=$fifth
 $full=Api-Status $connection '/api/chat/start' $fifthBody;if($full.Status-ne429-or-not$full.Body.Contains('worker_capacity')){throw("Capacity response mismatch: $($full.Status) $($full.Body)")}
 $queued=Api $connection '/api/task-queue' 'POST' @{sessionId=$fifth;text='capacity-fifth';kind='queued';request=$fifthBody}
 $deleteResponse=Invoke-WebRequest -UseBasicParsing -SkipHttpErrorCheck -Uri ($connection.Base+"/api/sessions/$fifth") -Headers $connection.Headers -Method DELETE -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes((@{workspace=$root;transcriptIds=@()}|ConvertTo-Json -Compress)))
 if([int]$deleteResponse.StatusCode-ne409-or-not([string]$deleteResponse.Content).Contains('session_has_background_work')){throw 'Queued session could be permanently deleted before its work started'}
 Api $connection "/api/chat/stop/$($runs[0].jobId)" 'POST'|Out-Null
 $script:stage='fifth Run start'
 $fifthRun=Wait-Until {try{$jsonText=(Invoke-WebRequest -UseBasicParsing -Uri ($connection.Base+'/api/chat/runs') -Headers $connection.Headers).Content;$array=[Newtonsoft.Json.Linq.JArray]::Parse($jsonText);foreach($token in $array){if([string]$token['sessionId'] -eq $fifth){return [string]$token['id']}}}catch{};''} {param($v) -not [string]::IsNullOrWhiteSpace($v)}
 $requestPath=Join-Path $root ".claude-gui-v2\runs\$fifthRun\request.json";$script:stage='fifth request evidence';$request=Wait-Until {try{Get-Content -LiteralPath $requestPath -Raw -Encoding UTF8|ConvertFrom-Json}catch{$null}} {param($v) $null -ne $v}
 if([bool]$request.resume -ne $false -or [int]$request.maxTurns -ne 137){throw('Queued request mutated resume/maxTurns: '+($request|ConvertTo-Json -Depth 8 -Compress))}
 foreach($run in @($runs|Select-Object -Skip 1)){Api $connection "/api/chat/stop/$($run.jobId)" 'POST'|Out-Null};Api $connection "/api/chat/stop/$fifthRun" 'POST'|Out-Null
 [pscustomobject]@{WorkerLimit=4;Capacity429='OK';AutomaticQueueContract='OK';QueuedDeletionGuard='OK';ResumePreserved=$true;MaxTurns=137;QueuedRunId=$fifthRun;Workspace=$root}|Format-List
}finally{if($connection -and $connection.Runtime.pid){Stop-Process -Id ([int]$connection.Runtime.pid) -Force -ErrorAction SilentlyContinue};Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath -eq $fake}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}}

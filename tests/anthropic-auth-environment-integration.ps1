param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root=[IO.Path]::GetFullPath((Join-Path $tempRoot ('anthropic-auth-env-'+[guid]::NewGuid().ToString('N'))))
if(-not$root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw'Unsafe test root'}
[IO.Directory]::CreateDirectory($root)|Out-Null
$fake=Join-Path $root 'fake-claude.exe'
$capture=Join-Path $root 'auth-capture.jsonl'
$hostProcess=$null

function Wait-Until([scriptblock]$Action,[scriptblock]$Predicate,[int]$Seconds=15){$expires=(Get-Date).AddSeconds($Seconds);do{Start-Sleep -Milliseconds 80;try{$value=&$Action}catch{$value=$null};if(&$Predicate $value){return $value}}while((Get-Date)-lt$expires);throw'Timed out waiting for Anthropic auth environment state'}
function Api($Connection,[string]$Path,[string]$Method='GET',$Body=$null){$r=@{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method;TimeoutSec=10};if($null-ne$Body){$r.ContentType='application/json; charset=utf-8';$r.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress))};Invoke-RestMethod @r}
function Run-Provider($Connection,[string]$Provider,[string]$Model,[string]$Prompt){$session=[guid]::NewGuid().ToString();$run=Api $Connection '/api/chat/start' 'POST' @{workspace=$root;prompt=$Prompt;sessionId=$session;claudeSessionId=$session;resume=$false;requestId=('auth-env-'+[guid]::NewGuid().ToString('N'));providerId=$Provider;model=$Model;effort='low';permissionMode='readonly';maxTurns=10;attachments=@();allowedDirs=@();allowedTools=@();disallowedTools=@()};Wait-Until {Api $Connection ('/api/chat/poll/'+$run.jobId)} {param($v)($v.status.terminalState??$v.status.state)-eq'completed'}|Out-Null;return $run.jobId}

try{
    $testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1');$csc = $testDependencies.Compiler;$json = $testDependencies.Json
    &$csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs');if($LASTEXITCODE-ne0){throw'Fake Claude build failed'};Copy-Item $json (Join-Path $root 'Newtonsoft.Json.dll')
    $env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='anthropic-auth-'+[guid]::NewGuid().ToString('N');$env:CLAUDE_GUI_AUTH_CAPTURE=$capture
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json';$runtime=Wait-Until {if(Test-Path $runtimePath){Get-Content $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}} {param($v)$null-ne$v-and$v.state-eq'running'}
    Add-Type -AssemblyName System.Security;$plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser);$connection=@{Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}}
    Api $connection '/api/providers' 'POST' @{id='anthropic-xkey';name='XKey';token='fixture-x-api-key';authStyle='x-api-key';text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@('x-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    Api $connection '/api/providers' 'POST' @{id='anthropic-bearer';name='Bearer';token='fixture-bearer-token';authStyle='bearer';text=@{enabled=$true;protocol='anthropic';baseUrl='http://127.0.0.1:9';models=@('b-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    $xRun=Run-Provider $connection 'anthropic-xkey' 'x-model' '中文 x-api-key 环境测试'
    $bRun=Run-Provider $connection 'anthropic-bearer' 'b-model' '中文 Bearer 环境测试'
    $records=Get-Content $capture -Encoding UTF8|ForEach-Object{$_|ConvertFrom-Json};$x=@($records|Where-Object prompt -like '*x-api-key*')[-1];$b=@($records|Where-Object prompt -like '*Bearer*')[-1]
    if($x.apiKey-ne'fixture-x-api-key'-or$x.authToken){throw'x-api-key Provider was not mapped to ANTHROPIC_API_KEY exclusively'}
    if($b.authToken-ne'fixture-bearer-token'-or$b.apiKey){throw'Bearer Provider was not mapped to ANTHROPIC_AUTH_TOKEN exclusively'}
    $xRequest=Get-Content (Join-Path $root ".claude-gui-v2\runs\$xRun\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if($xRequest.provider.authStyle-ne'x-api-key'){throw'Provider authStyle was not persisted into the durable Worker request'}
    [pscustomobject]@{AnthropicAuthEnvironment='PASS';XApiKeyExclusive=$true;BearerTokenExclusive=$true;AuthStylePersisted=$true;ChinesePromptRoundTrip=([string]$x.prompt).Contains('中文')}|Format-List
}
finally{if($hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue};Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_AUTH_CAPTURE -ErrorAction SilentlyContinue;if((Test-Path $root)-and$root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue}}

param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('image-api-resilience-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$portFile = Join-Path $root 'fixture-port.txt'
$stateFile = Join-Path $root 'fixture-state.json'
$fixture = $null
$hostProcess = $null
$token = 'sk-image-fixture-12345678'

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 15) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 80; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for image API resilience state'
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method; TimeoutSec=10 }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 16 -Compress)) }
    Invoke-RestMethod @request
}

try {
    $node = (Get-Command node -ErrorAction Stop).Source
    $fixture = Start-Process -FilePath $node -ArgumentList @((Join-Path $PSScriptRoot 'image-api-resilience-fixture.js'),$portFile,$stateFile) -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $port = Wait-Until { if(Test-Path $portFile){[int](Get-Content $portFile -Raw)} } { param($v) [int]$v -gt 0 }
    $env:CLAUDE_GUI_WORKSPACE=$root
    $env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE='1'
    $env:CLAUDE_GUI_MUTEX_SCOPE='image-api-resilience-'+[guid]::NewGuid().ToString('N')
    $env:CLAUDE_GUI_IMAGE_TIMEOUT_SECONDS='2'
    $hostProcess=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json'
    $runtime=Wait-Until {if(Test-Path $runtimePath){Get-Content $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}} {param($v)$null-ne$v-and$v.state-eq'running'}
    Add-Type -AssemblyName System.Security
    $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $connection=@{Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}}
    Api $connection '/api/providers' 'POST' @{
        id='image-resilience';name='Image Resilience';token=$token;authStyle='auto'
        text=@{enabled=$false;protocol='openai';baseUrl='';models=@()}
        image=@{enabled=$true;protocol='openai-images';baseUrl="http://127.0.0.1:$port/v1";models=@('auto-image','hang-image')}
    }|Out-Null

    $success=Api $connection '/api/image/start' 'POST' @{providerId='image-resilience';model='auto-image';prompt='中文生图链路';size='1024x1024';sessionId=[guid]::NewGuid().ToString()}
    $completed=Wait-Until {Api $connection ("/api/image/poll/"+$success.jobId)} {param($v)$v.state-eq'completed'} 8
    if(-not(Test-Path -LiteralPath $completed.outputPath)-or[IO.Path]::GetExtension([string]$completed.outputPath)-ne'.png'){throw 'Generated image was not persisted with detected PNG format'}
    $file=Invoke-WebRequest -UseBasicParsing -Uri ($connection.Base+'/api/image/file/'+$success.jobId) -Headers $connection.Headers -TimeoutSec 10
    if($file.Headers.'Content-Type'-notlike'image/png*'){throw 'Generated image endpoint returned the wrong MIME type'}
    $state=Get-Content $stateFile -Raw|ConvertFrom-Json
    if([int]$state.authFailures-lt1){throw 'Image auth auto mode did not exercise Bearer to x-api-key fallback'}

    $beforeClosed=[int]$state.closed
    $cancelRequestTarget=[int]$state.requests+2
    $cancelledRun=Api $connection '/api/image/start' 'POST' @{providerId='image-resilience';model='hang-image';prompt='取消测试';size='1024x1024';sessionId=[guid]::NewGuid().ToString()}
    Wait-Until {Get-Content $stateFile -Raw|ConvertFrom-Json} {param($v)[int]$v.requests-ge$cancelRequestTarget}|Out-Null
    $cancelTimer=[Diagnostics.Stopwatch]::StartNew();Api $connection ('/api/image/stop/'+$cancelledRun.jobId) 'POST' @{}|Out-Null
    $cancelled=Wait-Until {Api $connection ('/api/image/poll/'+$cancelledRun.jobId)} {param($v)$v.state-eq'cancelled'}
    Wait-Until {Get-Content $stateFile -Raw|ConvertFrom-Json} {param($v)[int]$v.closed-gt$beforeClosed}|Out-Null
    $cancelTimer.Stop()
    if($cancelTimer.ElapsedMilliseconds-gt3000-or$cancelled.outputPath){throw 'Cancelled image request remained active or wrote an output file'}

    $timeoutTimer=[Diagnostics.Stopwatch]::StartNew()
    $timeoutRun=Api $connection '/api/image/start' 'POST' @{providerId='image-resilience';model='hang-image';prompt='超时测试';size='1024x1024';sessionId=[guid]::NewGuid().ToString()}
    $timedOut=Wait-Until {Api $connection ('/api/image/poll/'+$timeoutRun.jobId)} {param($v)$v.state-eq'failed'} 7
    $timeoutTimer.Stop()
    if(-not([string]$timedOut.error).Contains('超时')-or$timeoutTimer.ElapsedMilliseconds-gt5000){throw 'Hanging image request was not bounded by timeout'}

    [pscustomobject]@{ImageApiResilience='PASS';AutoAuthFallback=$true;DetectedExtension=[IO.Path]::GetExtension([string]$completed.outputPath);Mime=$file.Headers.'Content-Type';CancelMs=$cancelTimer.ElapsedMilliseconds;TimeoutMs=$timeoutTimer.ElapsedMilliseconds;CancelledOutputWritten=$false}|Format-List
}
finally{
    if($hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}
    if($fixture-and-not$fixture.HasExited){Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_IMAGE_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
    if((Test-Path -LiteralPath $root)-and$root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
}

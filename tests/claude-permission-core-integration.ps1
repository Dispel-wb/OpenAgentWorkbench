param([Parameter(Mandatory=$true)][string]$Executable,[string]$Node='C:\Program Files\nodejs\node.exe')
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('permission-core-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$fixture=$null
$fixtureScript=Join-Path $PSScriptRoot 'claude-permission-core-fixture.js'
function Test-FixturePort([int]$Port){
    if($Port-lt1-or$Port-gt65535){return $false}
    $client=[Net.Sockets.TcpClient]::new()
    try{$async=$client.BeginConnect('127.0.0.1',$Port,$null,$null);if(-not$async.AsyncWaitHandle.WaitOne(1000)){return $false};$client.EndConnect($async);return $client.Connected}catch{return $false}finally{$client.Dispose()}
}
function Start-FixtureWithRetry([string]$PortFile){
    $attempts=[Collections.Generic.List[object]]::new()
    for($attempt=1;$attempt-le3;$attempt++){
        [IO.File]::Delete($PortFile)
        $stdout=Join-Path $root ("fixture-attempt-$attempt.stdout.log")
        $stderr=Join-Path $root ("fixture-attempt-$attempt.stderr.log")
        $process=$null;$port=0;$startedAt=[DateTimeOffset]::Now
        try{
            $process=Start-Process $Node -ArgumentList @($fixtureScript,$PortFile) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            $expires=(Get-Date).AddSeconds(15)
            do{
                Start-Sleep -Milliseconds 100;$process.Refresh()
                if(Test-Path -LiteralPath $PortFile){
                    $raw=[IO.File]::ReadAllText($PortFile).Trim()
                    if([int]::TryParse($raw,[ref]$port)-and-not$process.HasExited-and(Test-FixturePort $port)){
                        return [pscustomobject]@{Process=$process;Port=$port;Attempts=$attempt;Stdout=$stdout;Stderr=$stderr}
                    }
                }
            }while(-not$process.HasExited-and(Get-Date)-lt$expires)
        }catch{
            [IO.File]::AppendAllText($stderr,($_.Exception.ToString()+[Environment]::NewLine),[Text.UTF8Encoding]::new($false))
        }
        if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue;$process.WaitForExit(3000)|Out-Null}
        $attempts.Add([pscustomobject]@{
            attempt=$attempt;startedAt=$startedAt.ToString('o');durationMs=[Math]::Round(([DateTimeOffset]::Now-$startedAt).TotalMilliseconds)
            pid=$(if($process){$process.Id}else{0});exitCode=$(if($process-and$process.HasExited){$process.ExitCode}else{$null})
            portFileCreated=(Test-Path -LiteralPath $PortFile);port=$port
            stdout=$(if(Test-Path $stdout){[IO.File]::ReadAllText($stdout)}else{''});stderr=$(if(Test-Path $stderr){[IO.File]::ReadAllText($stderr)}else{''})
        })
        if($attempt-lt3){Start-Sleep -Milliseconds (500*$attempt)}
    }
    $evidenceRoot=Join-Path $PSScriptRoot '..\dist\permission-core'
    [IO.Directory]::CreateDirectory($evidenceRoot)|Out-Null
    $evidence=Join-Path $evidenceRoot ('fixture-startup-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'.json')
    [IO.File]::WriteAllText($evidence,([pscustomobject]@{state='failed';node=$Node;fixture=$fixtureScript;portFile=$PortFile;attempts=@($attempts)}|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    throw "Claude 离线夹具在 3 次有界尝试后仍未就绪；启动证据：$evidence"
}
try {
    $portFile=Join-Path $root 'port.txt'
    $started=Start-FixtureWithRetry $portFile
    $fixture=$started.Process;$port=$started.Port
    Add-Type -AssemblyName System.Security
    $protected=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-permission-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $provider=@{id='permission-fixture';name='Offline permission fixture';tokenEncrypted=$protected;authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl="http://127.0.0.1:$port/v1";models=@('fixture-tool-model')};image=@{enabled=$false;protocol='openai-images';models=@()}}
    [IO.File]::WriteAllText((Join-Path $root 'providers-v2.json'),(ConvertTo-Json -InputObject @($provider) -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'settings.json'),'{"providerId":"permission-fixture","model":"fixture-tool-model"}',[Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'live-workbench-acceptance.ps1') -Executable $Executable -SourceData $root -AllowPaid -Scenario all -VerifyBoundary -OutputDirectory (Join-Path $PSScriptRoot '..\dist\permission-core')
    Write-Output 'PASS genuine Claude core: MCP handshake, scoped Read/Edit, outside write denied, Chinese paths, second-turn recall, cross-parent DAG. No paid requests.'
}finally{
    if(Test-Path ($portFile+'.tools.json')){Copy-Item -LiteralPath ($portFile+'.tools.json') -Destination (Join-Path $PSScriptRoot '..\dist\permission-core\tools.json')}
    if(Test-Path ($portFile+'.failure.json')){Copy-Item -LiteralPath ($portFile+'.failure.json') -Destination (Join-Path $PSScriptRoot '..\dist\permission-core\failure.json')}
    if($fixture-and-not$fixture.HasExited){Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue}
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved).StartsWith('permission-core-')){Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue}
}

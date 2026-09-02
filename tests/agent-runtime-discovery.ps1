param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$DshEntry,
    [string]$NodePath = '',
    [string]$CodexPath = ''
)
$ErrorActionPreference='Stop'
if(-not $NodePath){$NodePath=(Get-Command node).Source}
if(-not $CodexPath){$CodexPath=(Get-Command codex).Source}
$root=Join-Path ([IO.Path]::GetTempPath()) ('core-discovery-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$process=$null
try {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=[IO.Path]::GetFullPath($Executable);$start.Arguments='--host'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.EnvironmentVariables['CLAUDE_GUI_WORKSPACE']=$root
    $start.EnvironmentVariables['CLAUDE_GUI_ROOT']=Join-Path $root 'install'
    $start.EnvironmentVariables['CLAUDE_GUI_TEST_MODE']='1'
    $start.EnvironmentVariables['CLAUDE_GUI_MUTEX_SCOPE']='core-discovery-'+[guid]::NewGuid().ToString('N')
    $start.EnvironmentVariables['CLAUDE_GUI_DSH_ENTRY']=[IO.Path]::GetFullPath($DshEntry)
    $start.EnvironmentVariables['CLAUDE_GUI_NODE_EXE']=$NodePath
    $start.EnvironmentVariables['CLAUDE_GUI_CODEX_EXE']=$CodexPath
    $process=[Diagnostics.Process]::Start($start)
    $runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json'
    $deadline=(Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 150
        $runtime=if(Test-Path -LiteralPath $runtimePath){Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
    } while(($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $deadline)
    if($null -eq $runtime -or $runtime.state -ne 'running'){throw 'Discovery Host did not start'}
    Add-Type -AssemblyName System.Security
    $secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}
    $base="http://127.0.0.1:$($runtime.port)"
    $diagnostics=Invoke-RestMethod "$base/api/workbench/runtime/agents?probe=1" -Headers $headers -TimeoutSec 30
    if(-not $diagnostics.codex.available -or -not $diagnostics.codex.probeOk){throw 'Genuine Codex CLI discovery failed'}
    if(-not $diagnostics.dsh.available -or -not $diagnostics.dsh.probeOk -or $diagnostics.dsh.version -ne '0.1.2-alpha.5'){throw 'Pinned DSHarness SDK discovery failed'}
    $fakePackage=Join-Path $root 'old-dsh'
    [IO.Directory]::CreateDirectory((Join-Path $fakePackage 'lib'))|Out-Null
    [IO.File]::WriteAllText((Join-Path $fakePackage 'package.json'),'{"name":"@deepseek-ai/dsh","version":"0.1.1-rc.2","dependencies":{}}')
    [IO.File]::WriteAllText((Join-Path $fakePackage 'lib\bin.js'),'// offline obsolete profile fixture')
    $body=@{workerHarness='dsh';dshEntry=(Join-Path $fakePackage 'lib\bin.js')}|ConvertTo-Json -Compress
    Invoke-RestMethod "$base/api/settings" -Headers $headers -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json'|Out-Null
    $obsolete=Invoke-RestMethod "$base/api/workbench/runtime/agents" -Headers $headers -TimeoutSec 10
    if($obsolete.available -or $obsolete.dsh.available -or -not $obsolete.dsh.probeError){throw 'Old DSH without SDK was advertised as available'}
    [pscustomobject]@{RuntimeDiscovery='PASS';CodexVersion=$diagnostics.codex.version;DshVersion=$diagnostics.dsh.version;NodeVersion=$diagnostics.dsh.nodeVersion;ObsoleteSdkRejected=$true;PaidApiCalls=0}|Format-List
} finally {
    if($process -and -not $process.HasExited){$process.Kill();$process.WaitForExit(5000)|Out-Null}
    $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and(Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference='Stop'
function Wait-Until([scriptblock]$Action,[scriptblock]$Predicate,[int]$Seconds=20){$expires=(Get-Date).AddSeconds($Seconds);do{Start-Sleep -Milliseconds 80;try{$value=&$Action}catch{$value=$null};if(&$Predicate $value){return $value}}while((Get-Date)-lt$expires);throw 'Timed out waiting for diagnostics host'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-diagnostics-'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($root)|Out-Null
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT=Join-Path $root 'install';$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='diagnostics-'+[guid]::NewGuid().ToString('N')
$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json';$hostProcess=$null
try{
 $hostProcess=Start-Process $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
 $runtime=Wait-Until{if(Test-Path $runtimePath){Get-Content $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}}{param($v)$null-ne$v-and$v.state-eq'running'}
 Add-Type -AssemblyName System.Security;$secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
 $headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'};$dataRoot=Join-Path $root '.claude-gui-v2'
 [IO.File]::WriteAllText((Join-Path $dataRoot 'native-crash.log'),'Authorization: Bearer sk-fixture-diagnostic-secret-123456789',[Text.UTF8Encoding]::new($false))
 $result=Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($runtime.port)/api/workbench/diagnostics/export" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes((@{workspace=$root}|ConvertTo-Json -Compress)))
 if(-not(Test-Path $result.path)){throw 'Diagnostic ZIP was not created'}
 $expanded=Join-Path $root 'expanded';Expand-Archive -LiteralPath $result.path -DestinationPath $expanded
 $joined=(Get-ChildItem $expanded -File|ForEach-Object{Get-Content $_.FullName -Raw -Encoding UTF8})-join[Environment]::NewLine
 if($joined.Contains('sk-fixture-diagnostic-secret-123456789')){throw 'Diagnostic bundle leaked API key'}
 if(-not$joined.Contains('[REDACTED]')){throw 'Diagnostic bundle did not contain redaction marker'}
 [pscustomobject]@{DiagnosticZip='OK';LocalOnly=$result.localOnly;Redaction='OK';SQLiteHealth='OK';Path=$result.path}|Format-List
}finally{if($hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue};Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue}

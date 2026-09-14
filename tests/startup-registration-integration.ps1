param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
function Wait-Until([scriptblock]$Action,[scriptblock]$Predicate,[int]$Seconds=20){
  $expires=(Get-Date).AddSeconds($Seconds)
  do{Start-Sleep -Milliseconds 100;try{$value=&$Action}catch{$value=$null};if(&$Predicate $value){return $value}}while((Get-Date)-lt$expires)
  throw("Timed out waiting for startup registration: "+$script:stage)
}
function Connect-Host([string]$RuntimePath,[int]$PreviousPid=0){$runtime=Wait-Until{if(Test-Path -LiteralPath $RuntimePath){Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8|ConvertFrom-Json}}{param($v)$null-ne$v-and$v.state-eq'running'-and[int]$v.pid-ne$PreviousPid};Add-Type -AssemblyName System.Security;$plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser);@{Runtime=$runtime;Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'}}}
function Api($Connection,[string]$Path,[string]$Method='GET',$Body=$null){$request=@{Uri=$Connection.Base+$Path;Headers=$Connection.Headers;Method=$Method};if($null-ne$Body){$request.ContentType='application/json; charset=utf-8';$request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 8 -Compress))};Invoke-RestMethod @request}
$root=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-startup-'+[guid]::NewGuid().ToString('N'))));[IO.Directory]::CreateDirectory($root)|Out-Null
$registryPath='Software\ClaudeCodeWorkbench\Tests\Startup-'+[guid]::NewGuid().ToString('N');$valueName='ClaudeCodeWorkbench.Native.Host';$runtimePath=Join-Path $root '.claude-gui-v2\runtime-state.json';$hostProcess=$null
try{
  $env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='startup-'+[guid]::NewGuid().ToString('N');$env:CLAUDE_GUI_STARTUP_REGISTRY_PATH=$registryPath
  $script:stage='initial host';$hostProcess=Start-Process $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath
  $edition=(Api $connection '/api/bootstrap').edition.id
  $valueName=if($edition-eq'opensource'){'OpenAgentWorkbench.Native.Host'}else{'ClaudeCodeWorkbench.Native.Host'}
  $initial=Api $connection '/api/workbench/startup';if($initial.enabled-or$initial.registered){throw 'Fresh startup registration was not disabled'}
  $health=Api $connection ('/api/workbench/health?workspace='+[Uri]::EscapeDataString($root));if($null-eq$health.loginStartup-or$health.loginStartup.launchMode-ne'host-only'){throw 'Diagnostics did not expose login-startup state'}
  $invalid=Invoke-WebRequest -UseBasicParsing -SkipHttpErrorCheck -Uri ($connection.Base+'/api/workbench/startup') -Headers $connection.Headers -Method POST -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes('{"enabled":"true"}'));if([int]$invalid.StatusCode-ne400){throw 'Non-boolean startup state was not rejected'}
  $enabled=Api $connection '/api/workbench/startup' 'POST' @{enabled=$true};if(-not$enabled.enabled-or-not$enabled.registered-or$enabled.launchMode-ne'host-only'){throw 'Host-only startup registration was not enabled'}
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath);try{$command=[string]$key.GetValue($valueName,'')}finally{$key.Dispose()};if($command-ne('"'+$Executable+'" --host')){throw 'Login startup command did not target the exact packaged Host'}
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath,$true);try{$key.SetValue($valueName,'"C:\stale\ClaudeCodeWorkbench.exe" --host',[Microsoft.Win32.RegistryValueKind]::String)}finally{$key.Dispose()}
  $oldPid=[int]$connection.Runtime.pid;Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath-eq$Executable}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue};Start-Sleep -Milliseconds 400
  $script:stage='host restart';$hostProcess=Start-Process $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru;$connection=Connect-Host $runtimePath $oldPid;$recovered=Api $connection '/api/workbench/startup';if(-not$recovered.registered-or$recovered.registeredExecutable-ne$Executable){throw 'Host restart did not reconcile a stale login registration'}
  $disabled=Api $connection '/api/workbench/startup' 'POST' @{enabled=$false};if($disabled.enabled-or$disabled.registered){throw 'Login startup registration was not disabled'}
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath);try{if($key-and$null-ne$key.GetValue($valueName,$null)){throw 'Registry value remained after disabling startup'}}finally{if($key){$key.Dispose()}}
  [pscustomobject]@{StartupRegistration='PASS';CurrentUserScope=$true;HostOnly=$true;DiagnosticsVisible=$true;InvalidStateBlocked=$true;ExactPackagedPath=$true;StalePathRepaired=$true;RestartReconciled=$true;DisableRemoved=$true;Workspace=$root}|Format-List
}
finally{
  Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath-eq$Executable}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
  try{[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryPath,$false)}catch{}
  Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_STARTUP_REGISTRY_PATH -ErrorAction SilentlyContinue
}

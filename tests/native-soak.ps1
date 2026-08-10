param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [double]$Hours = 72,
    [int]$SampleSeconds = 10,
    [int]$MaxPrivateMemoryMB = 900,
    [int]$MaxHandleGrowth = 1500,
    [string]$AttachRuntime = ''
)
$ErrorActionPreference = 'Stop'
if ($Hours -le 0 -or $Hours -gt 168) { throw 'Hours must be in (0, 168]' }
function Wait-Until([scriptblock]$Action,[scriptblock]$Predicate,[int]$Seconds=20){$expires=(Get-Date).AddSeconds($Seconds);do{Start-Sleep -Milliseconds 100;try{$value=&$Action}catch{$value=$null};if(&$Predicate $value){return $value}}while((Get-Date)-lt$expires);throw 'Timed out waiting for Host'}
$attached=-not[string]::IsNullOrWhiteSpace($AttachRuntime)
$root=if($attached){Split-Path (Split-Path ([IO.Path]::GetFullPath($AttachRuntime)) -Parent) -Parent}else{Join-Path ([IO.Path]::GetTempPath()) ('claude-soak-'+[guid]::NewGuid().ToString('N'))}
[IO.Directory]::CreateDirectory($root)|Out-Null
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$runtimePath=if($attached){[IO.Path]::GetFullPath($AttachRuntime)}else{Join-Path $root '.claude-gui-v2\runtime-state.json'}
$hostProcess=$null;$ownsHost=$false;$samples=0;$failures=0;$peakMemory=0L;$peakHandles=0;$initialHandles=0
try{
 if(-not$attached){$hostProcess=Start-Process $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru;$ownsHost=$true}
 $runtime=Wait-Until{if(Test-Path $runtimePath){Get-Content $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}}{param($v)$null-ne$v-and$v.state-eq'running'}
 if($attached){$hostProcess=Get-Process -Id ([int]$runtime.pid) -ErrorAction Stop}
 Add-Type -AssemblyName System.Security;$secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
 $headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'};$base="http://127.0.0.1:$($runtime.port)"
 $deadline=(Get-Date).AddHours($Hours);$initialHandles=(Get-Process -Id $runtime.pid).HandleCount
 while((Get-Date)-lt$deadline){
  try{
   $bootstrap=Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers $headers -TimeoutSec 5
   $health=Invoke-RestMethod -Uri "$base/api/workbench/health?workspace=$([uri]::EscapeDataString($root))" -Headers $headers -TimeoutSec 5
   if($bootstrap.persistence.integrity-ne'ok'-or-not$health.durableJobState){throw 'Health invariant failed'}
   $process=Get-Process -Id $runtime.pid -ErrorAction Stop;$memory=[long]$process.PrivateMemorySize64;$handles=[int]$process.HandleCount
   $peakMemory=[Math]::Max($peakMemory,$memory);$peakHandles=[Math]::Max($peakHandles,$handles)
   if($memory-gt$MaxPrivateMemoryMB*1MB){throw "Private memory exceeded $MaxPrivateMemoryMB MB"}
   if(($handles-$initialHandles)-gt$MaxHandleGrowth){throw "Handle growth exceeded $MaxHandleGrowth"}
   $samples++
  }catch{$failures++;throw}
  Start-Sleep -Seconds ([Math]::Max(1,$SampleSeconds))
 }
 [pscustomobject]@{Soak='PASS';RequestedHours=$Hours;Samples=$samples;Failures=$failures;PeakPrivateMemoryMB=[Math]::Round($peakMemory/1MB,1);InitialHandles=$initialHandles;PeakHandles=$peakHandles;SQLiteIntegrity='OK';Root=$root}|Format-List
}finally{if($ownsHost-and$hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}}

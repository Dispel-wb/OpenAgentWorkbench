param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$exe=[IO.Path]::GetFullPath($Executable)
$root=[IO.Path]::Combine([IO.Path]::GetTempPath(),'startup-first-frame-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$scope='startup-frame-'+[guid]::NewGuid().ToString('N')
$process=$null
try {
  $timer=[Diagnostics.Stopwatch]::StartNew()
  $env:CLAUDE_GUI_WORKSPACE=$root
  $env:CLAUDE_GUI_ROOT=$root
  $env:CLAUDE_GUI_TEST_MODE='1'
  $env:CLAUDE_GUI_MUTEX_SCOPE=$scope
  $process=Start-Process -FilePath $exe -ArgumentList '--ui' -WorkingDirectory $root -WindowStyle Hidden -PassThru
  $uiState=Join-Path $root '.claude-gui-v2\ui-connection-state.json'
  $runtimeState=Join-Path $root '.claude-gui-v2\runtime-state.json'
  $states=[Collections.Generic.List[string]]::new()
  $deadline=(Get-Date).AddSeconds(35)
  $connected=$null
  do {
    Start-Sleep -Milliseconds 25
    if(Test-Path -LiteralPath $uiState){
      try {$current=Get-Content -LiteralPath $uiState -Raw|ConvertFrom-Json}catch{$current=$null}
      if($current){
        $value=[string]$current.state
        if($states.Count-eq 0-or$states[$states.Count-1]-ne$value){$states.Add($value)}
        if($value-eq'connected'){$connected=$current;break}
      }
    }
    if($process.HasExited){throw "UI exited before first ready frame: $($process.ExitCode)"}
  } while((Get-Date)-lt$deadline)
  if(-not$connected){throw 'UI did not reach connected state'}
  $timer.Stop()
  if($timer.ElapsedMilliseconds-gt 6000){throw "First usable frame took too long: $($timer.ElapsedMilliseconds) ms"}
  if($states.Contains('reconnecting')){throw "Startup exposed a transient Host recovery state: $($states -join ', ')"}
  $runtime=Get-Content -LiteralPath $runtimeState -Raw|ConvertFrom-Json
  $hostProcess=Get-Process -Id ([int]$runtime.pid) -ErrorAction SilentlyContinue
  if(-not$hostProcess-or[datetime]$connected.updatedAt-lt[datetime]$runtime.updatedAt){throw 'Connected frame was published before the verified Host runtime'}
  [pscustomobject]@{startupFirstFrame='PASS';elapsedMs=$timer.ElapsedMilliseconds;states=@($states);hostPid=$runtime.pid;uiPid=$process.Id;connectedAt=$connected.updatedAt;webView2Mode=$connected.webView2Mode;webView2Version=$connected.webView2Version}|ConvertTo-Json -Depth 4
} finally {
  Get-CimInstance Win32_Process -ErrorAction SilentlyContinue|Where-Object{$_.ExecutablePath-eq$exe}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue}
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

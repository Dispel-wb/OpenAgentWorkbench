param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$exe=(Resolve-Path -LiteralPath $Executable).Path
$root=Join-Path $env:TEMP ('real-snapshot-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$scope='real-snapshot-'+[guid]::NewGuid().ToString('N')
$p1=$null;$p2=$null;$p3=$null;$hostPid=0
function Wait-Connected([string]$StateFile,[int]$OldPid=0){
  $limit=(Get-Date).AddSeconds(35)
  do {
    Start-Sleep -Milliseconds 100
    try{$value=Get-Content -LiteralPath $StateFile -Raw|ConvertFrom-Json}catch{$value=$null}
    if($value-and$value.state-eq'connected'-and[int]$value.uiPid-ne$OldPid){return $value}
  } while((Get-Date)-lt$limit)
  throw 'UI did not reach connected state'
}
try {
  $env:CLAUDE_GUI_WORKSPACE=$root
  $env:CLAUDE_GUI_ROOT=$root
  $env:CLAUDE_GUI_TEST_MODE='1'
  $env:CLAUDE_GUI_MUTEX_SCOPE=$scope
  $state=Join-Path $root '.claude-gui-v2\ui-connection-state.json'
  $p1=Start-Process -FilePath $exe -ArgumentList '--ui' -WorkingDirectory $root -WindowStyle Hidden -PassThru
  $s1=Wait-Connected $state
  $hostPid=[int]$s1.hostPid
  $snap=Join-Path $root '.claude-gui-v2\ui-startup-snapshot.png'
  $limit=(Get-Date).AddSeconds(8)
  do{Start-Sleep -Milliseconds 100}while(!(Test-Path -LiteralPath $snap)-and(Get-Date)-lt$limit)
  if(!(Test-Path -LiteralPath $snap)){throw 'Real startup snapshot was not captured'}
  $bytes=[IO.File]::ReadAllBytes($snap)
  if($bytes.Length-lt 10000-or$bytes[0]-ne137-or$bytes[1]-ne80-or$bytes[2]-ne78-or$bytes[3]-ne71){throw 'Captured startup snapshot is not a valid PNG'}
  Stop-Process -Id ([int]$s1.uiPid) -Force
  Start-Sleep -Milliseconds 500
  $p2=Start-Process -FilePath $exe -ArgumentList '--ui' -WorkingDirectory $root -PassThru
  $s2=Wait-Connected $state ([int]$s1.uiPid)
  if($s2.startupSnapshotLoaded-ne$true){throw 'Second launch did not load the real startup snapshot'}
  $existing=Get-Process -Id ([int]$s2.uiPid)
  if(-not $existing.CloseMainWindow()){throw 'Existing UI did not accept close-to-tray'}
  Start-Sleep -Milliseconds 500
  $existing.Refresh()
  if($existing.HasExited){throw 'Close-to-tray destroyed the UI/WebView process'}
  $p3=Start-Process -FilePath $exe -ArgumentList '--ui' -WorkingDirectory $root -WindowStyle Hidden -PassThru
  if(-not ($p3.WaitForExit(5000))){throw 'Tray launcher did not hand off to the existing UI process'}
  Start-Sleep -Milliseconds 400
  $restored=Get-Content -LiteralPath $state -Raw|ConvertFrom-Json
  if([int]$restored.uiPid -ne [int]$s2.uiPid){throw 'Tray restore created a replacement UI process'}
  [pscustomobject]@{realSnapshot='PASS';pngBytes=$bytes.Length;firstUiPid=$s1.uiPid;secondUiPid=$s2.uiPid;secondLaunchLoadedSnapshot=$s2.startupSnapshotLoaded;trayRestoreReusedUi=$true;hostPid=$s2.hostPid}|ConvertTo-Json
} finally {
  foreach($id in @($p1.Id,$p2.Id,$p3.Id,$hostPid)|Where-Object{$_}){Stop-Process -Id $id -Force -ErrorAction SilentlyContinue}
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

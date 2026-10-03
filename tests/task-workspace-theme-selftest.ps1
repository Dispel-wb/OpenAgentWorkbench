param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$testRoot=[IO.Path]::Combine([IO.Path]::GetTempPath(),'workbench-task-theme-'+[guid]::NewGuid().ToString('N'))
$workspace=Join-Path $testRoot 'work\Claude'
[IO.Directory]::CreateDirectory($workspace)|Out-Null
$process=$null
try {
  $env:CLAUDE_GUI_WORKSPACE=$workspace
  $env:CLAUDE_GUI_TEST_MODE='1'
  $env:CLAUDE_GUI_MUTEX_SCOPE='task-theme-'+[guid]::NewGuid().ToString('N')
  $process=Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $workspace -WindowStyle Hidden -PassThru
  $stateFile=Join-Path $workspace '.claude-gui-v2\runtime-state.json'
  $limit=(Get-Date).AddSeconds(20)
  do { Start-Sleep -Milliseconds 100; try {$state=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json}catch{$state=$null} } while(($null-eq$state-or$state.state-ne'running')-and(Get-Date)-lt$limit)
  if($null-eq$state){throw 'Host did not start'}
  Add-Type -AssemblyName System.Security
  $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$state.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
  $secret=[Text.Encoding]::UTF8.GetString($plain)
  $base="http://127.0.0.1:$($state.port)"
  $headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}
  $settings=@{workspace=$workspace;theme='light';skin='codex';workerHarness='codex'}|ConvertTo-Json
  Invoke-RestMethod -Method Post -Uri "$base/api/settings" -Headers $headers -ContentType 'application/json' -Body $settings|Out-Null
  $html=(Invoke-WebRequest -UseBasicParsing -Uri "$base/").Content
  if($html -notmatch '<html lang="zh-CN" data-theme="light" data-theme-mode="light" data-skin="codex"'){throw 'Saved theme was not present in the initial HTML response'}
  $id=[guid]::NewGuid().ToString()
  $body=@{sessionId=$id;title='tetris-game';harness='codex'}|ConvertTo-Json
  $task=Invoke-RestMethod -Method Post -Uri "$base/api/workbench/tasks/workspace" -Headers $headers -ContentType 'application/json' -Body $body
  $expectedRoot=[IO.Path]::GetFullPath((Join-Path $testRoot 'work\AgentCli\codex'))
  if([IO.Path]::GetFullPath([string]$task.root)-ne$expectedRoot-or-not(Test-Path -LiteralPath $task.workspace -PathType Container)){throw 'Task workspace was not created under its kernel root'}
  $sessions=ConvertTo-Json -InputObject @(@{id=$id;title='tetris-game';workspace=[string]$task.workspace;workspaceRoot=$workspace;started=$false})
  Invoke-RestMethod -Method Post -Uri "$base/api/sessions" -Headers $headers -ContentType 'application/json' -Body $sessions|Out-Null
  $bootstrap=Invoke-RestMethod -Method Get -Uri "$base/api/bootstrap" -Headers $headers
  if(([string]$bootstrap.sessions[0].workspace) -ne ([string]$task.workspace)){throw "Bootstrap replaced task workspace '$($task.workspace)' with '$($bootstrap.sessions[0].workspace)'"}
  [pscustomobject]@{taskWorkspace='PASS';initialTheme='PASS';workspace=$task.workspace}|ConvertTo-Json -Compress
} finally {
  if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}
  if(Test-Path -LiteralPath $testRoot){Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue}
  Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

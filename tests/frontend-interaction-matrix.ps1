param(
  [Parameter(Mandatory=$true)][string]$Executable,
  [Parameter(Mandatory=$true)][string]$Node,
  [Parameter(Mandatory=$true)][string]$NodeModules,
  [string]$BrowserExecutable='C:\Program Files\Google\Chrome\Application\chrome.exe',
  [string]$Output=''
)
$ErrorActionPreference='Stop'
$Output=if($Output){[IO.Path]::GetFullPath($Output)}else{Join-Path $PSScriptRoot 'frontend-interaction-matrix.png'}
$root=[IO.Path]::Combine([IO.Path]::GetTempPath(),'frontend-interaction-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$process=$null
try {
  $env:CLAUDE_GUI_WORKSPACE=$root
  $env:CLAUDE_GUI_ROOT=$root
  $env:CLAUDE_GUI_TEST_MODE='1'
  $env:CLAUDE_GUI_MUTEX_SCOPE='frontend-interaction-'+[guid]::NewGuid().ToString('N')
  $process=Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
  $stateFile=Join-Path $root '.claude-gui-v2\runtime-state.json';$limit=(Get-Date).AddSeconds(25)
  do{Start-Sleep -Milliseconds 100;try{$state=Get-Content $stateFile -Raw|ConvertFrom-Json}catch{$state=$null}}while(($null-eq$state-or$state.state-ne'running')-and(Get-Date)-lt$limit)
  if($null-eq$state){throw 'Frontend interaction Host did not start'}
  Add-Type -AssemblyName System.Security
  $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$state.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
  $env:CLAUDE_UI_BASE_URL="http://127.0.0.1:$($state.port)"
  $env:CLAUDE_UI_SECRET=[Text.Encoding]::UTF8.GetString($plain)
  $env:CLAUDE_UI_BROWSER_EXE=[IO.Path]::GetFullPath($BrowserExecutable)
  $env:CLAUDE_UI_SCREENSHOT=$Output
  $env:NODE_PATH=[IO.Path]::GetFullPath($NodeModules)
  & ([IO.Path]::GetFullPath($Node)) (Join-Path $PSScriptRoot 'frontend-interaction-matrix.js')
  if($LASTEXITCODE-ne0){throw 'Frontend interaction matrix failed'}
} finally {
  if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_BROWSER_EXE,Env:CLAUDE_UI_SCREENSHOT,Env:NODE_PATH -ErrorAction SilentlyContinue
}

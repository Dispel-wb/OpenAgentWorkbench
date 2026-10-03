param(
  [Parameter(Mandatory=$true)][string]$Executable,
  [Parameter(Mandatory=$true)][string]$Node,
  [Parameter(Mandatory=$true)][string]$NodeModules,
  [string]$BrowserExecutable='C:\Program Files\Google\Chrome\Application\chrome.exe'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::Combine([IO.Path]::GetTempPath(),'blockquote-theme-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$process=$null
try{
  $env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT=$root;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='blockquote-theme-'+[guid]::NewGuid().ToString('N')
  $process=Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
  $stateFile=Join-Path $root '.claude-gui-v2\runtime-state.json';$limit=(Get-Date).AddSeconds(20)
  do{Start-Sleep -Milliseconds 100;try{$state=Get-Content $stateFile -Raw|ConvertFrom-Json}catch{$state=$null}}while(($null-eq$state-or$state.state-ne'running')-and(Get-Date)-lt$limit)
  if($null-eq$state){throw 'Host did not start'}
  Add-Type -AssemblyName System.Security
  $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$state.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
  $env:CLAUDE_UI_BASE_URL="http://127.0.0.1:$($state.port)";$env:CLAUDE_UI_SECRET=[Text.Encoding]::UTF8.GetString($plain);$env:CLAUDE_UI_BROWSER_EXE=[IO.Path]::GetFullPath($BrowserExecutable);$env:NODE_PATH=[IO.Path]::GetFullPath($NodeModules)
  & ([IO.Path]::GetFullPath($Node)) (Join-Path $PSScriptRoot 'blockquote-theme-visual.js')
  if($LASTEXITCODE-ne 0){throw 'Blockquote theme visual test failed'}
}finally{
  if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue}
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_BROWSER_EXE,Env:NODE_PATH -ErrorAction SilentlyContinue
}

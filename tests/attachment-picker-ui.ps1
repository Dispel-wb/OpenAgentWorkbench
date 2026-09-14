param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Node,
    [Parameter(Mandatory=$true)][string]$NodeModules,
    [string]$BrowserExecutable='C:\Program Files\Google\Chrome\Application\chrome.exe'
)
$ErrorActionPreference='Stop'
$testRoot=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('attachment-picker-ui-'+[guid]::NewGuid().ToString('N'))))
$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if(-not $testRoot.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test root'}
[IO.Directory]::CreateDirectory($testRoot)|Out-Null
$hostProcess=$null
try {
    $env:CLAUDE_GUI_WORKSPACE=$testRoot
    $env:CLAUDE_GUI_ROOT=Join-Path $testRoot '应用安装'
    $env:CLAUDE_GUI_TEST_MODE='1'
    $env:CLAUDE_GUI_MUTEX_SCOPE='attachment-picker-'+[guid]::NewGuid().ToString('N')
    $hostProcess=Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $runtimePath=Join-Path $testRoot '.claude-gui-v2\runtime-state.json';$expires=(Get-Date).AddSeconds(20);$runtime=$null
    do {Start-Sleep -Milliseconds 120;try{$runtime=if(Test-Path -LiteralPath $runtimePath){Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}}catch{$runtime=$null}} while(($null-eq$runtime-or$runtime.state-ne'running')-and(Get-Date)-lt$expires)
    if($null-eq$runtime-or$runtime.state-ne'running'){throw 'Attachment picker Host did not start'}
    Add-Type -AssemblyName System.Security
    $plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $env:CLAUDE_UI_BASE_URL="http://127.0.0.1:$($runtime.port)"
    $env:CLAUDE_UI_SECRET=[Text.Encoding]::UTF8.GetString($plain)
    $env:CLAUDE_UI_WORKSPACE=$testRoot
    $env:CLAUDE_UI_BROWSER_EXE=[IO.Path]::GetFullPath($BrowserExecutable)
    $env:NODE_PATH=[IO.Path]::GetFullPath($NodeModules)
    & $Node (Join-Path $PSScriptRoot 'attachment-picker-ui.js')
    if($LASTEXITCODE-ne0){throw 'Attachment picker UI test failed'}
} finally {
    if($hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_WORKSPACE,Env:CLAUDE_UI_BROWSER_EXE,Env:NODE_PATH -ErrorAction SilentlyContinue
    if((Test-Path -LiteralPath $testRoot)-and$testRoot.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $testRoot -Recurse -Force}
}

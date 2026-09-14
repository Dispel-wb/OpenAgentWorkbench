param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Node,
    [Parameter(Mandatory = $true)][string]$NodeModules,
    [string]$BrowserExecutable = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string]$OutputDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-long-session-' + [guid]::NewGuid().ToString('N'))))
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'long-session-' + [guid]::NewGuid().ToString('N')
    $hostProcess = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $runtime = $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Real-conversation Host did not start' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $env:CLAUDE_UI_BASE_URL = "http://127.0.0.1:$($runtime.port)"
    $env:CLAUDE_UI_SECRET = [Text.Encoding]::UTF8.GetString($plain)
    $env:CLAUDE_UI_OUTPUT_DIR = [IO.Path]::GetFullPath($OutputDir)
    $env:CLAUDE_UI_WORKSPACE = $root
    $env:CLAUDE_UI_BROWSER_EXE = [IO.Path]::GetFullPath($BrowserExecutable)
    $env:NODE_PATH = [IO.Path]::GetFullPath($NodeModules)
    & $Node (Join-Path $PSScriptRoot 'real-conversation-benchmark.js')
    if ($LASTEXITCODE -ne 0) { throw 'Real-conversation UI visual test failed' }
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_OUTPUT_DIR,Env:CLAUDE_UI_WORKSPACE,Env:CLAUDE_UI_BROWSER_EXE,Env:NODE_PATH -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

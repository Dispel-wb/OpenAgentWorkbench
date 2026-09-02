param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Node,
    [Parameter(Mandatory = $true)][string]$NodeModules,
    [string]$BrowserExecutable = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string]$OutputDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 25) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 100; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw 'Timed out waiting for Provider UI visual Host'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-provider-ui-' + [guid]::NewGuid().ToString('N'))
$root = [IO.Path]::GetFullPath($root)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe Provider UI fixture root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$executablePath = [IO.Path]::GetFullPath($Executable)
$outputPath = [IO.Path]::GetFullPath($OutputDir)
if (-not (Test-Path -LiteralPath $BrowserExecutable)) { throw 'Chrome executable was not found' }
$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'provider-ui-' + [guid]::NewGuid().ToString('N')
$hostProcess = $null
try {
    $hostProcess = Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
    $runtime = Wait-Until { if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $env:CLAUDE_UI_BASE_URL = "http://127.0.0.1:$($runtime.port)"
    $env:CLAUDE_UI_SECRET = [Text.Encoding]::UTF8.GetString($plain)
    $env:CLAUDE_UI_OUTPUT_DIR = $outputPath
    $env:CLAUDE_UI_WORKSPACE = $root
    $env:CLAUDE_UI_BROWSER_EXE = [IO.Path]::GetFullPath($BrowserExecutable)
    $env:NODE_PATH = [IO.Path]::GetFullPath($NodeModules)
    & $Node (Join-Path $PSScriptRoot 'provider-health-ui-visual.js')
    if ($LASTEXITCODE -ne 0) { throw "Provider UI visual test failed with exit code $LASTEXITCODE" }
} finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $executablePath } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Node,
    [Parameter(Mandatory = $true)][string]$NodeModules,
    [string]$BrowserExecutable = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string]$OutputDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
function Wait-Runtime([string]$Path) {
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $value = $null }
    } while (($null -eq $value -or $value.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $value -or $value.state -ne 'running') { throw 'Responsive UI Host did not start' }
    return $value
}

$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-responsive-ui-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$executablePath = [IO.Path]::GetFullPath($Executable)
$hostProcess = $null
$startupRegistryPath = 'Software\ClaudeCodeWorkbench\Tests\Responsive-' + [guid]::NewGuid().ToString('N')
try {
    $env:CLAUDE_GUI_WORKSPACE = $testRoot
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'responsive-ui-' + [guid]::NewGuid().ToString('N')
    $env:CLAUDE_GUI_STARTUP_REGISTRY_PATH = $startupRegistryPath
    $hostProcess = Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $runtime = Wait-Runtime (Join-Path $testRoot '.claude-gui-v2\runtime-state.json')
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $env:CLAUDE_UI_BASE_URL = "http://127.0.0.1:$($runtime.port)"
    $env:CLAUDE_UI_SECRET = [Text.Encoding]::UTF8.GetString($plain)
    $env:CLAUDE_UI_OUTPUT_DIR = [IO.Path]::GetFullPath($OutputDir)
    $env:CLAUDE_UI_WORKSPACE = $testRoot
    $env:CLAUDE_UI_BROWSER_EXE = [IO.Path]::GetFullPath($BrowserExecutable)
    $env:NODE_PATH = [IO.Path]::GetFullPath($NodeModules)
    & $Node (Join-Path $PSScriptRoot 'ui-responsive-visual.js')
    if ($LASTEXITCODE -ne 0) { throw 'Responsive UI visual test failed' }
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($startupRegistryPath, $false) } catch { }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_STARTUP_REGISTRY_PATH,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_OUTPUT_DIR,Env:CLAUDE_UI_WORKSPACE,Env:CLAUDE_UI_BROWSER_EXE,Env:NODE_PATH -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $testRoot) -and $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

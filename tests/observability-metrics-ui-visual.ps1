param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Node,
    [Parameter(Mandatory = $true)][string]$NodeModules,
    [string]$BrowserExecutable = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string]$OutputDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
function Wait-Runtime([string]$path) {
    $until = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $value = $null }
    } while (($null -eq $value -or $value.state -ne 'running') -and (Get-Date) -lt $until)
    if ($null -eq $value -or $value.state -ne 'running') { throw 'Metrics UI Host did not start' }
    return $value
}
$root=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-metrics-ui-'+[guid]::NewGuid().ToString('N'))));[IO.Directory]::CreateDirectory($root)|Out-Null
$executablePath=[IO.Path]::GetFullPath($Executable);$hostProcess=$null
try {
    $env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT='D:\softwares\ClaudeCode';$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='metrics-ui-'+[guid]::NewGuid().ToString('N')
    $hostProcess=Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtime=Wait-Runtime (Join-Path $root '.claude-gui-v2\runtime-state.json')
    Add-Type -AssemblyName System.Security;$plain=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $env:CLAUDE_UI_BASE_URL="http://127.0.0.1:$($runtime.port)";$env:CLAUDE_UI_SECRET=[Text.Encoding]::UTF8.GetString($plain);$env:CLAUDE_UI_OUTPUT_DIR=[IO.Path]::GetFullPath($OutputDir);$env:CLAUDE_UI_WORKSPACE=$root;$env:CLAUDE_UI_BROWSER_EXE=[IO.Path]::GetFullPath($BrowserExecutable);$env:NODE_PATH=[IO.Path]::GetFullPath($NodeModules)
    & $Node (Join-Path $PSScriptRoot 'observability-metrics-ui-visual.js'); if ($LASTEXITCODE -ne 0) { throw 'Metrics UI visual test failed' }
} finally {
    if($hostProcess-and-not$hostProcess.HasExited){Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue}
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_OUTPUT_DIR,Env:CLAUDE_UI_WORKSPACE,Env:CLAUDE_UI_BROWSER_EXE,Env:NODE_PATH -ErrorAction SilentlyContinue
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue}
}

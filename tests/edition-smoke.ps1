param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][ValidateSet('local','opensource')][string]$ExpectedEdition
)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('agent-edition-smoke-' + [guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$executablePath = [IO.Path]::GetFullPath($Executable)
$env:CLAUDE_GUI_WORKSPACE = $testRoot
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$process = $null
try {
    $process = Start-Process -FilePath $executablePath -ArgumentList '--host' -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $testRoot '.claude-gui-v2\runtime-state.json'
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 150
        $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Edition Host did not start' }

    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$runtime.authProtected), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers = @{'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2'}
    $base = "http://127.0.0.1:$($runtime.port)"
    $bootstrap = Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers $headers -TimeoutSec 8
    if ($bootstrap.edition.id -ne $ExpectedEdition) { throw "Expected $ExpectedEdition edition, got $($bootstrap.edition.id)" }
    if ([bool]$bootstrap.edition.openSource -ne ($ExpectedEdition -eq 'opensource')) { throw 'Edition openSource flag mismatch' }
    if ($bootstrap.persistence.integrity -ne 'ok') { throw 'SQLite integrity failed' }

    $node = 'C:\Program Files\nodejs\node.exe'
    & $node (Join-Path $PSScriptRoot 'native_offline_smoke.js') $runtime.port $secret
    if ($LASTEXITCODE -ne 0) { throw 'Offline edition smoke failed' }

    [pscustomobject]@{
        EditionSmoke = 'PASS'
        Edition = $bootstrap.edition.id
        ProductName = $bootstrap.edition.productName
        Version = $bootstrap.version
        OpenSource = [bool]$bootstrap.edition.openSource
        Workspace = $bootstrap.workspace
        HostPid = $runtime.pid
        SQLiteIntegrity = $bootstrap.persistence.integrity
    } | Format-List
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $testRoot) -and $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

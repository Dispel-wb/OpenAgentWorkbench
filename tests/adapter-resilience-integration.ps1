param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('adapter-resilience-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$portFile = Join-Path $root 'fixture-port.txt'
$stateFile = Join-Path $root 'fixture-state.json'
$node = 'C:\Program Files\nodejs\node.exe'
$fixture = $null
$hostProcess = $null
$fake = Join-Path $root 'adapter-run-cancel-worker.exe'

function Wait-File([string]$Path, [int]$Seconds = 15) {
    $expires = (Get-Date).AddSeconds($Seconds)
    while (-not (Test-Path -LiteralPath $Path) -and (Get-Date) -lt $expires) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $Path)) { throw "Timed out waiting for $Path" }
}

try {
    $vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
    $csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
    & $csc /nologo /target:exe /platform:x64 "/out:$fake" /reference:System.dll /reference:System.Core.dll /reference:System.Net.Http.dll (Join-Path $PSScriptRoot 'adapter-run-cancel-worker.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Adapter cancellation Worker build failed' }
    $fixture = Start-Process -FilePath $node -ArgumentList @((Join-Path $PSScriptRoot 'adapter-resilience-fixture.js'), $portFile, $stateFile) -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Wait-File $portFile
    $fixturePort = [int](Get-Content -LiteralPath $portFile -Raw)

    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_CLAUDE_EXE = $fake
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'adapter-resilience-' + [guid]::NewGuid().ToString('N')
    $env:CLAUDE_GUI_ADAPTER_HEADER_TIMEOUT_SECONDS = '2'
    $env:CLAUDE_GUI_ADAPTER_IDLE_TIMEOUT_SECONDS = '2'
    $env:CLAUDE_GUI_MAX_REQUEST_BODY_BYTES = '8192'
    $hostProcess = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
    Wait-File $runtimePath
    $expires = (Get-Date).AddSeconds(15)
    do {
        try { $runtime = Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $runtime = $null }
        if ($null -eq $runtime -or $runtime.state -ne 'running') { Start-Sleep -Milliseconds 100 }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Adapter resilience Host did not start' }

    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $apiHeaders = @{'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2'}
    $provider = @{
        id = 'adapter-resilience'; name = 'Adapter resilience'; token = 'sk-fixture-secret-12345678'; authStyle = 'auto'
        text = @{ enabled = $true; protocol = 'openai'; baseUrl = "http://127.0.0.1:$fixturePort/v1"; models = @('bad-model','bad-body-hang-model','auto-xkey-model','no-stream-options-model','hang-model','idle-model','stream-error-model','truncated-model') }
        image = @{ enabled = $false; protocol = 'openai-images'; baseUrl = ''; models = @() }
    }
    Invoke-RestMethod -Uri "http://127.0.0.1:$($runtime.port)/api/providers" -Method POST -Headers $apiHeaders -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($provider | ConvertTo-Json -Depth 8 -Compress))) -TimeoutSec 10 | Out-Null

    Push-Location $root
    try { & $node (Join-Path $PSScriptRoot 'adapter-resilience-client.js') $runtime.port $secret $stateFile }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $stateFile) { Write-Host ('Fixture state: ' + (Get-Content -LiteralPath $stateFile -Raw)) }
        throw 'Adapter resilience client failed'
    }
    & $node (Join-Path $PSScriptRoot 'native_adapter_smoke.js') $runtime.port $secret
    if ($LASTEXITCODE -ne 0) { throw 'Existing Adapter conversion smoke failed' }
    & $node (Join-Path $PSScriptRoot 'provider-protocol-matrix.js') $runtime.port $secret
    if ($LASTEXITCODE -ne 0) { throw 'Provider protocol matrix failed' }
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:CLAUDE_GUI_ADAPTER_HEADER_TIMEOUT_SECONDS,Env:CLAUDE_GUI_ADAPTER_IDLE_TIMEOUT_SECONDS,Env:CLAUDE_GUI_MAX_REQUEST_BODY_BYTES -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

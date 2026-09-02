param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-runtime-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'claude.exe'
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')

function Wait-Host([string]$Path) {
    $expires = (Get-Date).AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; if (Test-Path -LiteralPath $Path) { try { $value = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $value = $null }; if ($value.state -eq 'running') { return $value } } } while ((Get-Date) -lt $expires)
    throw 'Runtime integration Host did not start'
}
function Api($connection,[string]$path,[string]$method='GET',$body=$null) {
    $arguments = @{ Uri=$connection.Base+$path; Headers=$connection.Headers; Method=$method }
    if ($null -ne $body) { $arguments.ContentType='application/json; charset=utf-8'; $arguments.Body=[Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 12 -Compress)) }
    Invoke-RestMethod @arguments
}

$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = Join-Path $root 'empty-install'
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'claude-runtime-' + [guid]::NewGuid().ToString('N')
$process = $null
try {
    $process = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtime = Wait-Host (Join-Path $root '.claude-gui-v2\runtime-state.json')
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $connection = @{ Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
    $configured = Api $connection '/api/workbench/runtime/claude/configure' 'POST' @{path=$fake}
    if (-not $configured.available -or -not $configured.probeOk -or $configured.source -ne 'settings' -or $configured.version -notmatch 'fake-claude') { throw 'Configured Claude runtime was not persisted and probed' }
    $health = Api $connection "/api/workbench/health?workspace=$([uri]::EscapeDataString($root))"
    if (-not $health.claudeRuntime.available -or $health.claudeRuntime.path -ne $fake) { throw 'Health endpoint did not expose Claude runtime evidence' }
    $command = Api $connection '/api/workbench/runtime/claude/command' 'POST' @{workspace=$root;kind='mcp-list'}
    if ($command.exitCode -ne 0 -or $command.output -notmatch 'management command ok' -or $command.runtimePath -ne $fake) { throw 'Claude management command did not use the configured runtime' }
    $reset = Api $connection '/api/workbench/runtime/claude/reset' 'POST' @{}
    if ($reset.configuredPath) { throw 'Runtime reset retained the manual path' }
    [pscustomobject]@{ClaudeRuntime='PASS';ConfiguredSource=$configured.source;ProbeVersion=$configured.version;HealthEvidence='OK';ManagementCommand='OK';Reset='OK';Workspace=$root} | Format-List
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
}

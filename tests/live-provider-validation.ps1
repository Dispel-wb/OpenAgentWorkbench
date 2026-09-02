param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$SourceData = 'D:\work\Claude\.claude-gui-v2'
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$sourceProviders = Join-Path $SourceData 'providers-v2.json'
$sourceSettings = Join-Path $SourceData 'settings.json'
if (-not (Test-Path -LiteralPath $sourceProviders) -or -not (Test-Path -LiteralPath $sourceSettings)) { throw 'Saved Provider configuration is unavailable' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-live-provider-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe live Provider test root' }
$data = Join-Path $root '.claude-gui-v2'
[IO.Directory]::CreateDirectory($data) | Out-Null
Copy-Item -LiteralPath $sourceProviders -Destination (Join-Path $data 'providers-v2.json')
Copy-Item -LiteralPath $sourceSettings -Destination (Join-Path $data 'settings.json')
$process = $null
try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'live-provider-' + [guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtimePath = Join-Path $data 'runtime-state.json'
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $runtime = $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $deadline)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Live Provider Host did not start' }
    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers = @{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}
    $base = "http://127.0.0.1:$($runtime.port)"
    $bootstrap = Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers $headers -TimeoutSec 10
    $providerId = [string]$bootstrap.settings.providerId
    $model = [string]$bootstrap.settings.model
    if ([string]::IsNullOrWhiteSpace($providerId) -or [string]::IsNullOrWhiteSpace($model)) { throw 'Current Provider/model selection is incomplete' }
    $configured = @($bootstrap.providers | Where-Object { $_.id -eq $providerId }) | Select-Object -First 1
    if ($null -eq $configured -or -not @($configured.text.models | Where-Object { $_ -eq $model }).Count) { throw 'Current Provider/model is not present in the saved configuration' }
    $body = [Text.Encoding]::UTF8.GetBytes((@{providerId=$providerId;model=$model}|ConvertTo-Json -Compress))
    $result = Invoke-RestMethod -Method Post -Uri "$base/api/providers/validate-model" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 45
    if (-not [bool]$result.ok -or -not [bool]$result.chatVerified) { throw 'Live Provider validation did not verify chat capability' }
    [pscustomobject]@{LiveProviderValidation='PASS';Provider=$providerId;Model=$model;LatencyMs=[long]$result.latencyMs;ChatVerified=[bool]$result.chatVerified;ToolsVerified=[bool]$result.toolsVerified;InputTokens=[int]$result.inputTokens;OutputTokens=[int]$result.outputTokens;AuthStyle=[string]$result.authStyle;IsolatedData=$true;SecretPrinted=$false} | Format-List
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

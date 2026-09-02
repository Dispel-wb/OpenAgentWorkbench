param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-http-resources-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe HTTP resource test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$process = $null
function Snapshot-Process([int]$Id) {
    $value = Get-Process -Id $Id -ErrorAction Stop
    [pscustomobject]@{Handles=[int]$value.HandleCount;Threads=[int]$value.Threads.Count;PrivateMB=[math]::Round($value.PrivateMemorySize64/1MB,1)}
}
try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'http-resources-' + [guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $runtime = $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $deadline)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'HTTP resource Host did not start' }
    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $env:RESOURCE_BASE = "http://127.0.0.1:$($runtime.port)"
    $env:RESOURCE_SECRET = $secret
    $env:RESOURCE_COUNT = '1500'
    $env:RESOURCE_CONCURRENCY = '12'
    Start-Sleep -Seconds 1
    $baseline = Snapshot-Process ([int]$runtime.pid)
    & node (Join-Path $PSScriptRoot 'http-resource-stability-client.js') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'HTTP resource phase one failed' }
    Start-Sleep -Seconds 2
    $phaseOne = Snapshot-Process ([int]$runtime.pid)
    & node (Join-Path $PSScriptRoot 'http-resource-stability-client.js') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'HTTP resource phase two failed' }
    Start-Sleep -Seconds 3
    $phaseTwo = Snapshot-Process ([int]$runtime.pid)
    if ($phaseTwo.Handles -gt $phaseOne.Handles + 64) { throw "HTTP handles did not plateau: phase1=$($phaseOne.Handles), phase2=$($phaseTwo.Handles)" }
    if ($phaseTwo.Threads -gt $phaseOne.Threads + 8) { throw "HTTP worker threads did not plateau: phase1=$($phaseOne.Threads), phase2=$($phaseTwo.Threads)" }
    if ($phaseTwo.PrivateMB -gt $phaseOne.PrivateMB + 20) { throw "HTTP private memory did not plateau: phase1=$($phaseOne.PrivateMB)MB, phase2=$($phaseTwo.PrivateMB)MB" }
    $headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}
    $metrics=Invoke-RestMethod -Uri "$($env:RESOURCE_BASE)/api/workbench/metrics" -Headers $headers
    if ([long]$metrics.http.currentSession.total -lt 3000) { throw 'HTTP resource stress requests were not recorded' }
    [pscustomobject]@{HttpResourceStability='PASS';Requests=3000;BaselineHandles=$baseline.Handles;PhaseOneHandles=$phaseOne.Handles;PhaseTwoHandles=$phaseTwo.Handles;BaselineThreads=$baseline.Threads;PhaseOneThreads=$phaseOne.Threads;PhaseTwoThreads=$phaseTwo.Threads;BaselinePrivateMB=$baseline.PrivateMB;PhaseOnePrivateMB=$phaseOne.PrivateMB;PhaseTwoPrivateMB=$phaseTwo.PrivateMB} | Format-List
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:RESOURCE_BASE,Env:RESOURCE_SECRET,Env:RESOURCE_COUNT,Env:RESOURCE_CONCURRENCY -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-http-disconnect-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
$data = Join-Path $root '.claude-gui-v2'
[IO.Directory]::CreateDirectory($data) | Out-Null
$runtimePath = Join-Path $data 'runtime-state.json'
$process = $null
try {
    # A large bootstrap response makes the server write after the client has sent RST.
    $title = '中文断连夹具' + ('x' * 700)
    $entry = '{"id":"disconnect-fixture","title":"' + $title + '","workspace":"' + ($root.Replace('\','\\')) + '"}'
    $entries = ((($entry + ',') * 12000) -join '').TrimEnd(',')
    [IO.File]::WriteAllText((Join-Path $data 'sessions.json'), '[' + $entries + ']', [Text.UTF8Encoding]::new($false))
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'http-disconnect-' + [guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $runtime = $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Host did not start' }
    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $request = [Text.Encoding]::ASCII.GetBytes("GET /api/bootstrap HTTP/1.1`r`nHost: 127.0.0.1:$($runtime.port)`r`nX-Desktop-Secret: $secret`r`nX-Workbench-Protocol: 2`r`nConnection: close`r`n`r`n")
    for ($index = 0; $index -lt 24; $index++) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $client.NoDelay = $true
            $client.Client.LingerState = [Net.Sockets.LingerOption]::new($true, 0)
            $client.Connect('127.0.0.1', [int]$runtime.port)
            $stream = $client.GetStream(); $stream.Write($request, 0, $request.Length); $stream.Flush()
        } finally { $client.Close() }
    }
    Start-Sleep -Milliseconds 1400
    if ($process.HasExited) { throw "Host exited after client disconnects: $($process.ExitCode)" }
    $crashLog = Join-Path $data 'native-crash.log'
    $crashes = if (Test-Path -LiteralPath $crashLog) { Get-Content -LiteralPath $crashLog -Raw -Encoding UTF8 } else { '' }
    if ($crashes -match 'HttpRoute|不存在的网络连接|nonexistent network connection') { throw 'Expected client disconnect was recorded as a Host crash' }
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$($runtime.port)/api/workbench/health" -Headers @{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'} -TimeoutSec 10
    if (-not [bool]$health.durableJobState -or $health.version -ne '6.4.21-dev.native.local') { throw 'Host did not remain healthy after client disconnects' }
    [pscustomobject]@{ HttpClientDisconnect='PASS'; AbruptDisconnects=24; CrashMetricPollution=$false; HostAlive=$true; DurableJobState=$health.durableJobState; Workspace=$root } | Format-List
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

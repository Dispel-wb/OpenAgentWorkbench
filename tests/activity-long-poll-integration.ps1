param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-activity-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe activity test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$process = $null
try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'activity-' + [guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $runtime = $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $deadline)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Activity Host did not start' }

    Add-Type -AssemblyName System.Security
    $secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $sessionId = [guid]::NewGuid().ToString()
    $jobId = [guid]::NewGuid().ToString()
    $runDir = Join-Path $root ".claude-gui-v2\runs\$jobId"
    [IO.Directory]::CreateDirectory($runDir) | Out-Null
    $request = @{guiSessionId=$sessionId;sessionId=$sessionId;sourceWorkspace=$root;workspace=$root;model='offline-activity'} | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $runDir 'request.json'), $request, [Text.UTF8Encoding]::new($false))
    $policy = @{schemaVersion=1;jobId=$jobId;mode='manual';workspace=$root;roots=@($root);writeRoots=@($root);capabilities=@{read='allow';write='ask';delete='ask';execute='ask';network='ask';unknown='ask'};allowedTools=@();disallowedTools=@()} | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText((Join-Path $runDir 'security-policy.json'), $policy, [Text.UTF8Encoding]::new($false))

    $env:ACTIVITY_BASE = "http://127.0.0.1:$($runtime.port)"
    $env:ACTIVITY_SECRET = $secret
    $env:ACTIVITY_SESSION = $sessionId
    $env:ACTIVITY_JOB = $jobId
    $env:ACTIVITY_WORKSPACE = $root
    & node (Join-Path $PSScriptRoot 'activity-long-poll-client.js')
    if ($LASTEXITCODE -ne 0) { throw "Activity long-poll client failed: $LASTEXITCODE" }
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:ACTIVITY_BASE,Env:ACTIVITY_SECRET,Env:ACTIVITY_SESSION,Env:ACTIVITY_JOB,Env:ACTIVITY_WORKSPACE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

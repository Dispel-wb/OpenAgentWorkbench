param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('host-start-failure-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
$process=$null
try {
    $workspace=Join-Path $scratch 'not-a-directory'
    [IO.File]::WriteAllText($workspace,'fixture: workspace is a file')
    $start=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable),'--host')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.Environment['CLAUDE_GUI_WORKSPACE']=$workspace
    $start.Environment['CLAUDE_GUI_ROOT']=$scratch
    $start.Environment['CLAUDE_GUI_TEST_MODE']='1'
    $start.Environment['CLAUDE_GUI_MUTEX_SCOPE']='startup-fault-'+[guid]::NewGuid().ToString('N')
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $process=[Diagnostics.Process]::Start($start)
    if(-not $process.WaitForExit(5000)){throw 'Background Host remained alive after startup failure (possible modal dialog)'}
    if($process.ExitCode -ne 1){throw "Wrong startup failure exit code: $($process.ExitCode)"}
    [pscustomobject]@{HostStartupFailure='PASS';ExitCode=1;ElapsedMs=$timer.ElapsedMilliseconds}|ConvertTo-Json -Compress
} finally {
    if($process -and -not $process.HasExited){$process.Kill();$process.WaitForExit(3000)|Out-Null}
    $resolved=[IO.Path]::GetFullPath($scratch)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved).StartsWith('host-start-failure-')){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

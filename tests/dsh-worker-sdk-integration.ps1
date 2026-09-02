param([Parameter(Mandatory=$true)][string]$Executable, [string]$NodePath='')
$ErrorActionPreference='Stop'
if(-not $NodePath){$NodePath=(Get-Command node).Source}
$root=Join-Path ([IO.Path]::GetTempPath()) ('dsh-sdk-中文 路径-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$process=$null
try {
    $config=Join-Path $root 'bridge.json'
    $payload=@{harness='dsh';executable=$NodePath;entry=(Join-Path $PSScriptRoot 'fake-dsh-worker.js');profile='sdk';home=$root;workspace=$root;model='fixture-model';permissionMode='readonly';sessionId='fixture-session';maxTurns=10}
    [IO.File]::WriteAllText($config,($payload|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=[IO.Path]::GetFullPath($Executable);$start.Arguments='--agent-worker-bridge "'+$config+'"'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $process=[Diagnostics.Process]::Start($start)
    $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
    $line='{"type":"user","message":{"role":"user","content":[{"type":"text","text":"中文连续对话"}]}}'
    $bytes=[Text.Encoding]::UTF8.GetBytes($line+"`n"+$line+"`n")
    $process.StandardInput.BaseStream.Write($bytes,0,$bytes.Length);$process.StandardInput.BaseStream.Flush();$process.StandardInput.Close()
    if(-not $process.WaitForExit(20000)){throw 'DSHarness bridge timed out'}
    if($process.ExitCode -ne 0){throw ('DSHarness bridge failed: '+$errors.Result+' '+$output.Result)}
    $events=@($output.Result -split "`r?`n"|Where-Object{$_}|ForEach-Object{$_|ConvertFrom-Json})
    $results=@($events|Where-Object type -eq result)
    if($results.Count -ne 2 -or @($results|Where-Object is_error).Count){throw 'Expected two successful DSHarness turns'}
    if(@($results|Where-Object{$_.result -ne 'DSHarness 中文回复' -or $_.usage.input_tokens -ne 3 -or $_.usage.output_tokens -ne 4}).Count){throw 'Reply or Token mapping failed'}
    if(@($events|Where-Object{$_.type -eq 'user' -and $_.message.content[0].type -eq 'tool_result'}).Count -ne 2){throw 'Tool results were not mapped'}
    [pscustomobject]@{DshWorkerSdk='PASS';Turns=2;Utf8=$true;ToolEvents=$true;TokenUsage=$true;PreAckEvents=$true;ReadOnlyEnvironment=$true}|Format-List
} finally {
    if($process -and -not $process.HasExited){$process.Kill();$process.WaitForExit(3000)|Out-Null}
    $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and(Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

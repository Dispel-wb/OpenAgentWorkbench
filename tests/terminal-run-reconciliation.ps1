param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$root = [IO.Path]::GetFullPath((Join-Path $tempRoot ('claude-terminal-reconcile-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$data = Join-Path $root '.claude-gui-v2'
$runtimePath = Join-Path $data 'runtime-state.json'
$hostProcess = $null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TerminalReconcileSqlite {
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, byte[] vfs);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr statement, int index);
    static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }
    static string Text(IntPtr value, int bytes) { if (value == IntPtr.Zero || bytes <= 0) return ""; var data = new byte[bytes]; Marshal.Copy(value, data, 0, bytes); return Encoding.UTF8.GetString(data); }
    public static void Execute(string path, string sql) { IntPtr db; if (sqlite3_open_v2(Utf8(path), out db, 2, null) != 0) throw new Exception("SQLite open failed"); try { IntPtr error; if (sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out error) != 0) throw new Exception("SQLite execute failed"); } finally { sqlite3_close_v2(db); } }
    public static string Scalar(string path, string sql) { IntPtr db, statement; if (sqlite3_open_v2(Utf8(path), out db, 1, null) != 0) throw new Exception("SQLite open failed"); try { if (sqlite3_prepare_v2(db, Utf8(sql), -1, out statement, IntPtr.Zero) != 0) throw new Exception("SQLite prepare failed"); try { return sqlite3_step(statement) == 100 ? Text(sqlite3_column_text(statement, 0), sqlite3_column_bytes(statement, 0)) : ""; } finally { sqlite3_finalize(statement); } } finally { sqlite3_close_v2(db); } }
}
'@

function Start-TestHost {
    if (Test-Path -LiteralPath $runtimePath) { Remove-Item -LiteralPath $runtimePath -Force }
    $script:hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $expires = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $runtime = if (Test-Path -LiteralPath $runtimePath) { Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null } } catch { $runtime = $null }
    } while (($null -eq $runtime -or $runtime.state -ne 'running') -and (Get-Date) -lt $expires)
    if ($null -eq $runtime -or $runtime.state -ne 'running') { throw 'Host did not start' }
}

function Stop-TestHost {
    if ($script:hostProcess -and -not $script:hostProcess.HasExited) { Stop-Process -Id $script:hostProcess.Id -Force -ErrorAction SilentlyContinue; $script:hostProcess.WaitForExit(5000) | Out-Null }
    $script:hostProcess = $null
}

try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'terminal-reconcile-' + [guid]::NewGuid().ToString('N')
    Start-TestHost
    Stop-TestHost

    $utf8 = [Text.UTF8Encoding]::new($false)
    $db = Join-Path $data 'agent-store.db'
    $stamp = [DateTimeOffset]::Now.ToString('o')
    $cases = @(
        [pscustomobject]@{ Id='terminal-reconcile-failed'; Disk='failed'; Job='queued'; Database='running' },
        [pscustomobject]@{ Id='terminal-reconcile-completed'; Disk='completed'; Job='queued'; Database='running' },
        [pscustomobject]@{ Id='terminal-reconcile-conflict'; Disk='failed'; Job='cancelled'; Database='cancelled' }
    )
    foreach ($case in $cases) {
        $runRoot = Join-Path (Join-Path $data 'runs') $case.Id
        [IO.Directory]::CreateDirectory($runRoot) | Out-Null
        [IO.File]::WriteAllText((Join-Path $runRoot 'request.json'), '{"guiSessionId":"fixture-session","workspace":"' + ($root.Replace('\','\\')) + '","model":"offline-fixture"}', $utf8)
        [IO.File]::WriteAllText((Join-Path $runRoot 'status.json'), ('{"state":"' + $case.Disk + '","message":"fixture terminal state"}'), $utf8)
        [IO.File]::WriteAllText((Join-Path $runRoot 'job-state.json'), ('{"schemaVersion":1,"id":"' + $case.Id + '","kind":"chat","state":"' + $case.Job + '","turnStartSeq":0}'), $utf8)
        [TerminalReconcileSqlite]::Execute($db, "INSERT OR REPLACE INTO runs(id,task_id,model,workspace_path,state,worker_kind,worker_pid,created_at,updated_at,source_json) VALUES('$($case.Id)','fixture-session','offline-fixture','fixture','$($case.Database)','native',999999,'$stamp','$stamp','{}')")
    }

    Start-TestHost
    $expires = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $pending = @($cases | Where-Object { $runRoot = Join-Path (Join-Path $data 'runs') $_.Id; $jobState = Get-Content -LiteralPath (Join-Path $runRoot 'job-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json; $databaseState = [TerminalReconcileSqlite]::Scalar($db, "SELECT state FROM runs WHERE id='$($_.Id)'" ); $jobState.state -ne $_.Disk -or $databaseState -ne $_.Disk })
    } while ($pending.Count -gt 0 -and (Get-Date) -lt $expires)
    foreach ($case in $cases) {
        $runRoot = Join-Path (Join-Path $data 'runs') $case.Id
        $jobState = Get-Content -LiteralPath (Join-Path $runRoot 'job-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $databaseState = [TerminalReconcileSqlite]::Scalar($db, "SELECT state FROM runs WHERE id='$($case.Id)'")
        if ($jobState.state -ne $case.Disk) { throw "$($case.Id): job-state.json did not reconcile to $($case.Disk)" }
        if ($databaseState -ne $case.Disk) { throw "$($case.Id): SQLite did not reconcile to $($case.Disk)" }
        if (Test-Path -LiteralPath (Join-Path $runRoot 'child.pid')) { throw "$($case.Id): terminal Run was incorrectly restarted" }
    }
    $crashLog = Join-Path $data 'native-crash.log'
    if ((Test-Path -LiteralPath $crashLog) -and (Get-Content -LiteralPath $crashLog -Raw -Encoding UTF8) -match 'RestoreTerminalState') { throw 'Terminal reconciliation polluted crash metrics' }
    [pscustomobject]@{ TerminalRunReconciliation='PASS'; Cases=$cases.Count; QueuedToCompleted='PASS'; ConflictingTerminal='PASS'; CrashMetricPollution=$false; WorkerRestarted=$false; Workspace=$root } | Format-List
}
finally {
    Stop-TestHost
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

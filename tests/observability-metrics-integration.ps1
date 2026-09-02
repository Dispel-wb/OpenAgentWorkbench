param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'

function Wait-Runtime([string]$path) {
    $until = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        try { $value = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { $value = $null }
    } while (($null -eq $value -or $value.state -ne 'running') -and (Get-Date) -lt $until)
    if ($null -eq $value -or $value.state -ne 'running') { throw 'Metrics Host did not start' }
    return $value
}

function Get-Headers($runtime) {
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{'X-Desktop-Secret' = [Text.Encoding]::UTF8.GetString($plain); 'X-Workbench-Protocol' = '2'}
}

$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-metrics-' + [guid]::NewGuid().ToString('N'))))
[IO.Directory]::CreateDirectory($root) | Out-Null
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$process = $null
try {
    $env:CLAUDE_GUI_WORKSPACE = $root
    $env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'observability-' + [guid]::NewGuid().ToString('N')
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtime = Wait-Runtime $runtimePath
    $headers = Get-Headers $runtime
    $base = "http://127.0.0.1:$($runtime.port)"
    $before = Invoke-RestMethod -Uri "$base/api/workbench/metrics" -Headers $headers
    $waitRouteBeforeTotal = [long]$before.http.total
    $waitRouteBeforeDurations = [long]$before.http.durations.count
    Invoke-RestMethod -Method Post -Uri "$base/api/workbench/projects/add" -Headers $headers -ContentType 'application/json' -Body (@{path=$root}|ConvertTo-Json -Compress) | Out-Null
    $afterWaitRoute = Invoke-RestMethod -Uri "$base/api/workbench/metrics" -Headers $headers
    if ([long]$afterWaitRoute.http.total -lt $waitRouteBeforeTotal + 2) { throw 'User-wait route was not counted in HTTP totals' }
    if ([long]$afterWaitRoute.http.durations.count -ne $waitRouteBeforeDurations + 1) { throw 'User-wait route polluted HTTP latency samples' }
    try { Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers @{'X-Desktop-Secret'='invalid';'X-Workbench-Protocol'='2'} | Out-Null } catch { }
    $validationStatus = 0
    try { Invoke-RestMethod -Method Post -Uri "$base/api/workbench/update/config" -Headers $headers -ContentType 'application/json' -Body (@{channel='stable';manifestUrl='http://example.invalid/release.json'}|ConvertTo-Json -Compress)|Out-Null } catch { $validationStatus = [int]$_.Exception.Response.StatusCode }
    Invoke-RestMethod -Uri "$base/api/workbench/health?workspace=$([uri]::EscapeDataString($root))" -Headers $headers | Out-Null
    $after = Invoke-RestMethod -Uri "$base/api/workbench/metrics" -Headers $headers
    if ([long]$after.http.total -le [long]$before.http.total) { throw 'HTTP metrics did not increase' }
    if ([long]$after.http.errors -lt [long]$before.http.errors + 1) { throw 'HTTP error counter did not increase' }
    if ($null -eq $after.http.durations.p95Ms) { throw 'HTTP percentile summary is missing' }
    if ($null -eq $after.http.currentSession -or [long]$after.http.currentSession.total -le [long]$before.http.currentSession.total -or [long]$after.http.currentSession.errors -lt [long]$before.http.currentSession.errors + 1 -or $null -eq $after.http.currentSession.p95Ms) { throw 'Current Host HTTP metrics are missing or did not increase' }
    if ($null -eq $after.runs.started -or $null -eq $after.host.starts) { throw 'Run/Host metrics shape is missing' }
    if ($null -eq $after.runs.currentSession -or $null -eq $after.runs.currentSession.completed -or $null -eq $after.runs.currentSession.active) { throw 'Current Host Run metrics shape is missing' }
    if ($validationStatus -ne 400 -or [long]$after.host.crashes -ne [long]$before.host.crashes) { throw 'Expected request validation was incorrectly recorded as a Host crash' }
    if ($null -eq $after.host.currentSession -or $null -eq $after.host.currentSession.handledErrors -or [long]$after.host.currentSession.crashes -ne 0) { throw 'Current Host crash/handled-error baseline is missing or polluted by history' }
    $metricsPath = Join-Path $root '.claude-gui-v2\native-metrics.json'
    $writeBeforeBurst = (Get-Item -LiteralPath $metricsPath).LastWriteTimeUtc
    1..20 | ForEach-Object { Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers $headers | Out-Null }
    Start-Sleep -Milliseconds 250
    $writeDuringBurst = (Get-Item -LiteralPath $metricsPath).LastWriteTimeUtc
    if ($writeDuringBurst -ne $writeBeforeBurst) { throw 'High-frequency requests caused immediate per-request metric writes' }
    $flushDeadline=(Get-Date).AddSeconds(5);$writeAfterBatch=$writeDuringBurst
    do { Start-Sleep -Milliseconds 150;$writeAfterBatch=(Get-Item -LiteralPath $metricsPath).LastWriteTimeUtc } while($writeAfterBatch-le$writeDuringBurst-and(Get-Date)-lt$flushDeadline)
    if ($writeAfterBatch -le $writeDuringBurst) { throw 'Batched metrics were not flushed after the debounce window' }
    $firstTotal = [long]$after.http.total
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
    $process = $null
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 250
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtime2 = Wait-Runtime $runtimePath
    $headers2 = Get-Headers $runtime2
    $persisted = Invoke-RestMethod -Uri "http://127.0.0.1:$($runtime2.port)/api/workbench/metrics" -Headers $headers2
    if ([long]$persisted.http.total -lt $firstTotal) { throw 'HTTP metrics were not persisted across Host restart' }
    if ([long]$persisted.host.starts -lt 2) { throw 'Host start counter was not persisted' }
    if ([long]$persisted.host.currentSession.crashes -ne 0 -or [long]$persisted.host.currentSession.workerErrors -ne 0 -or [long]$persisted.host.currentSession.handledErrors -ne 0) { throw 'Historical failures leaked into the current Host counters' }
    if ([long]$persisted.http.currentSession.errors -ne 0 -or [long]$persisted.http.currentSession.total -gt 1) { throw 'Historical HTTP failures leaked into the current Host counters' }
    if ([long]$persisted.runs.currentSession.completed -ne 0 -or [long]$persisted.runs.currentSession.failed -ne 0 -or [long]$persisted.runs.currentSession.cancelled -ne 0) { throw 'Historical Run outcomes leaked into the current Host counters' }
    $bundle = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($runtime2.port)/api/workbench/diagnostics/export" -Headers $headers2 -ContentType 'application/json' -Body (@{workspace=$root} | ConvertTo-Json -Compress)
    if (-not (Test-Path -LiteralPath $bundle.path)) { throw 'Diagnostics bundle missing' }
    $expanded = Join-Path $root 'expanded'; Expand-Archive -LiteralPath $bundle.path -DestinationPath $expanded
    if (-not (Test-Path -LiteralPath (Join-Path $expanded 'native-metrics.sanitized.json'))) { throw 'Metrics file missing from diagnostics bundle' }
    $joined = (Get-ChildItem $expanded -File | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }) -join [Environment]::NewLine
    if ($joined -match '(?i)(api[_-]?key|authorization|token)\s*[:=]\s*(?!\[REDACTED\])') { throw 'Diagnostics metrics contain an unredacted secret field' }
    [pscustomobject]@{ Metrics = 'PASS'; HttpRequests = $persisted.http.total; HttpErrors = $persisted.http.errors; CurrentHttpRequests=$persisted.http.currentSession.total;CurrentHttpErrors=$persisted.http.currentSession.errors;UserWaitLatencyExcluded=$true;ValidationStatus=$validationStatus;ValidationCrashCount='unchanged';CurrentHostCrashes=$persisted.host.currentSession.crashes;CurrentRunCompleted=$persisted.runs.currentSession.completed;HostStarts = $persisted.host.starts; DiagnosticsMetrics = 'OK' } | Format-List
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

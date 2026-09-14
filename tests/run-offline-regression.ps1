param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$DependencyRoot=(Join-Path $PSScriptRoot '..\.packages'),
    [ValidateRange(30,1800)][int]$TestTimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$DependencyRoot=(Resolve-Path -LiteralPath $DependencyRoot).Path
$source=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory to preserve earlier regression evidence' }
[IO.Directory]::CreateDirectory($output)|Out-Null
$pwsh=(Get-Command pwsh -ErrorAction Stop).Source
$node=(Get-Command node -ErrorAction Stop).Source
$hash=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
$cases=[Collections.Generic.List[object]]::new()
function Add-Case([string]$Name,[string]$File,[object[]]$Arguments=@(),[bool]$NeedsExe=$true) {
    $cases.Add(@{name=$Name;file=(Join-Path $PSScriptRoot $File);arguments=$Arguments;needsExe=$NeedsExe})
}
$hostTests=@(
    'activity-long-poll-integration','adapter-resilience-integration','anthropic-auth-environment-integration',
    'background-job-reconciliation','background-loop-fault-isolation','child-agent-integration','claude-runtime-integration',
    'compaction-evidence-integration','context-budget-integration','extension-trust-integration',
    'http-client-disconnect-integration','http-resource-stability-integration','image-api-resilience-integration',
    'model-capability-integration','model-discovery-resilience-integration','multi-session-run-center-integration',
    'native-agent-fault-stress','native-agent-integration','native-host-watchdog-integration','native-installer-selftest',
    'native-m2-integration','native-m3-queue-integration','native-update-handoff-integration','native-updater-selftest',
    'native-worker-selftest','observability-metrics-integration','permission-broker-restart-integration',
    'provider-health-integration','run-evidence-integration','run-preparation-failure-integration','schedule-run-integration',
    'startup-registration-integration','task-workspace-selftest','terminal-profile-integration','terminal-run-reconciliation',
    'tool-runtime-integration','worker-capacity-queue-integration','worker-stream-pressure','workflow-integration',
    'workspace-memory-integration','event-store-selftest','task-security-selftest','skill-catalog-selftest',
    'native-diagnostics-selftest','host-startup-failure-selftest','native-soak-recovery-integration',
    'native-soak-resilience-integration','native-soak-status-integration'
)
foreach($name in $hostTests){
    $hostArguments=if($name-eq'native-agent-fault-stress'){@('-ObserverDelayMilliseconds','500')}else{@()}
    Add-Case $name "$name.ps1" $hostArguments
}
foreach($name in @('product-gaps-selftest','webview-runtime-selftest','sdk-frame-reader-selftest')){Add-Case $name "$name.ps1" @('-DependencyRoot',$DependencyRoot) $false}
foreach($name in @('release-ci-gate-selftest','release-signature-policy-selftest','v1-readiness-selftest')){Add-Case $name "$name.ps1" @() $false}
Add-Case 'agent-worker-sdk-integration' 'agent-worker-sdk-integration.ps1' @('-DependencyRoot',$DependencyRoot)
Add-Case 'terminal-poll-race-selftest' 'terminal-poll-race-selftest.ps1'
Add-Case 'pi-host-integration' 'pi-host-integration.ps1' @('-PiEntry',(Join-Path $source 'runtimes/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'))
Add-Case 'pi-host-resource-stability' 'pi-host-integration.ps1' @(
    '-PiEntry',(Join-Path $source 'runtimes/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js'),
    '-CycleLimit','60','-ResourceWarmupCycles','20','-MaxHandleGrowth','1000','-MaxPostWarmupHandleGrowth','100'
)
Add-Case 'edition-smoke' 'edition-smoke.ps1' @('-ExpectedEdition','opensource','-NodePath',$node)
foreach($fault in @('','oversize','flood','byte-flood','malformed','eof')){
    $argsForCase=@('-NodePath',$node)
    if($fault){$argsForCase+=@('-Fault',$fault)}
    Add-Case ('dsh-sdk-'+$(if($fault){$fault}else{'normal'})) 'dsh-worker-sdk-integration.ps1' $argsForCase
}
foreach($name in @('ui-contract-selftest','ui-connection-state-selftest','bootstrap-workspace-selftest','provider-auto-match-selftest','public-doc-links-selftest')){Add-Case $name "$name.js" @() $false}
Add-Case 'pi-worker-sdk-integration' 'pi-worker-sdk-integration.js'
$report=[ordered]@{
    schemaVersion=1;state='running';candidateSha256=$hash;startedAt=[DateTimeOffset]::UtcNow.ToString('o');completedAt=$null
    scope='Offline regression only; not clean-Windows, native UI/accessibility, paid-provider, security-review or 72-hour certification'
    total=$cases.Count;passed=0;failed=0;results=@();artifactIntegrity='pending'
    testFiles=@(Get-ChildItem -LiteralPath $PSScriptRoot -File|Where-Object Extension -in @('.ps1','.js','.cs')|ForEach-Object{@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
}
function Save-Report {
    [IO.File]::WriteAllText((Join-Path $output 'regression.json'),($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
}
Save-Report
foreach($case in $cases){
    # Each test gets its own binary path: legacy cleanup by executable path cannot kill another test or the installed app.
    $caseRoot=Join-Path $output $case.name
    [IO.Directory]::CreateDirectory($caseRoot)|Out-Null
    $testExe=Join-Path $caseRoot ([IO.Path]::GetFileName($Executable))
    if($case.needsExe){Copy-Item -LiteralPath $Executable -Destination $testExe}
    $isJs=[IO.Path]::GetExtension($case.file)-eq'.js'
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=if($isJs){$node}else{$pwsh}
    $start.WorkingDirectory=$source;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.Environment['WORKBENCH_TEST_DEPENDENCY_ROOT']=$DependencyRoot
    # Do not let the developer's provider migration defaults satisfy offline fixtures.
    foreach($key in @('ANTHROPIC_API_KEY','ANTHROPIC_AUTH_TOKEN','ANTHROPIC_BASE_URL')) { $start.Environment.Remove($key) | Out-Null }
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    if(-not$isJs){foreach($arg in @('-NoProfile','-NonInteractive','-File')){$start.ArgumentList.Add($arg)}}
    $start.ArgumentList.Add($case.file)
    if($case.needsExe){if(-not$isJs){$start.ArgumentList.Add('-Executable')};$start.ArgumentList.Add($testExe)}
    foreach($arg in $case.arguments){$start.ArgumentList.Add([string]$arg)}
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    # Keep all 100 fault cycles: hosted Windows needs more than five minutes.
    $caseTimeout=if($case.name-eq'native-agent-fault-stress'){[Math]::Max($TestTimeoutSeconds,900)}elseif($case.name-eq'pi-host-resource-stability'){[Math]::Max($TestTimeoutSeconds,600)}else{$TestTimeoutSeconds}
    $timedOut=-not$process.WaitForExit($caseTimeout*1000)
    if($timedOut){$process.Kill($true);$process.WaitForExit()}
    [IO.File]::WriteAllText((Join-Path $caseRoot 'stdout.txt'),$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $caseRoot 'stderr.txt'),$stderr.GetAwaiter().GetResult())
    $ok=-not$timedOut-and$process.ExitCode-eq0
    $report.results+=@{name=$case.name;status=$(if($ok){'passed'}else{'failed'});exitCode=$process.ExitCode;timedOut=$timedOut;seconds=[Math]::Round($clock.Elapsed.TotalSeconds,2);scriptSha256=(Get-FileHash -LiteralPath $case.file -Algorithm SHA256).Hash}
    if($ok){$report.passed++}else{$report.failed++}
    Save-Report
    Write-Output "$($report.results.Count)/$($report.total) $($case.name): $($report.results[-1].status)"
    $process.Dispose()
}
$changed=@($report.testFiles|Where-Object{(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_.name) -Algorithm SHA256).Hash-ne$_.sha256})
$report.artifactIntegrity=if((Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash-eq$hash-and$changed.Count-eq0){'passed'}else{'failed'}
$report.completedAt=[DateTimeOffset]::UtcNow.ToString('o')
$report.state=if($report.failed-or$report.artifactIntegrity-ne'passed'){'failed'}else{'passed'}
Save-Report
if($report.state-ne'passed'){throw "$($report.failed) cases failed; artifact integrity: $($report.artifactIntegrity); see $output"}

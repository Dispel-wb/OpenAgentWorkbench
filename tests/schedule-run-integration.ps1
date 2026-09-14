param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$NodeExecutable = '',
    [string]$PlaywrightRoot = '',
    [string]$BrowserExecutable = '',
    [string]$VisualOutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$visualArguments = @($NodeExecutable, $PlaywrightRoot, $BrowserExecutable, $VisualOutputDirectory) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
if ($visualArguments.Count -gt 0 -and $visualArguments.Count -ne 4) { throw 'Visual schedule evidence verification requires NodeExecutable, PlaywrightRoot, BrowserExecutable, and VisualOutputDirectory together.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-schedule-run-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$fake = Join-Path $root 'fake-claude.exe'
$testDependencies = & (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1')
$csc = $testDependencies.Compiler
$json = $testDependencies.Json
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" (Join-Path $PSScriptRoot 'fake-claude-worker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake Claude build failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')
function Wait-Host([string]$Path) {
    $expires = (Get-Date).AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; if (Test-Path -LiteralPath $Path) { try { $value = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $value = $null }; if ($value.state -eq 'running') { return $value } } } while ((Get-Date) -lt $expires)
    throw 'Schedule integration Host did not start'
}
function Api($connection,[string]$path,[string]$method='GET',$body=$null) {
    $arguments=@{Uri=$connection.Base+$path;Headers=$connection.Headers;Method=$method}
    if($null-ne$body){$arguments.ContentType='application/json; charset=utf-8';$arguments.Body=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $body -Depth 15 -Compress))}
    Invoke-RestMethod @arguments
}
function Wait-Schedule($connection,[string]$id,[string]$state,[int]$Seconds=45) {
    $expires=(Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 250
        $target=$null
        try {
            $response=Api $connection '/api/workbench/schedules'
            $items=if($null -ne $response -and $null -ne $response.PSObject.Properties['value']){@($response.value)}else{@($response)}
            foreach($item in $items){if([string]$item.id -eq $id){$target=$item;break}}
        } catch { $target=$null }
        if($null -ne $target -and [string]$target.state -eq $state){return $target}
    } while((Get-Date)-lt$expires)
    throw "Timed out waiting for schedule $id to reach $state"
}
function Stop-TestUi([string]$Root,[string]$ExecutablePath) {
    $statePath=Join-Path $Root '.claude-gui-v2\ui-connection-state.json'
    if(-not(Test-Path -LiteralPath $statePath)){return}
    try {
        $state=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8|ConvertFrom-Json
        $uiPid=[int]$state.uiPid
        $ui=Get-CimInstance Win32_Process -Filter "ProcessId=$uiPid" -ErrorAction SilentlyContinue
        if($null-ne$ui-and[string]::Equals([string]$ui.ExecutablePath,$ExecutablePath,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $uiPid -Force -ErrorAction SilentlyContinue}
    } catch { }
}
$env:CLAUDE_GUI_WORKSPACE=$root;$env:CLAUDE_GUI_ROOT=(Join-Path $root 'empty-install');$env:CLAUDE_GUI_CLAUDE_EXE=$fake;$env:CLAUDE_GUI_TEST_MODE='1';$env:CLAUDE_GUI_MUTEX_SCOPE='schedule-run-'+[guid]::NewGuid().ToString('N')
$process=$null
try {
    $process=Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $runtime=Wait-Host (Join-Path $root '.claude-gui-v2\runtime-state.json')
    Add-Type -AssemblyName System.Security
    $secret=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $connection=@{Base="http://127.0.0.1:$($runtime.port)";Headers=@{'X-Desktop-Secret'=$secret;'X-Workbench-Protocol'='2'}}
    Api $connection '/api/providers' 'POST' @{id='offline-schedule';name='Offline Schedule';token='schedule-token';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('offline-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    Api $connection '/api/providers' 'POST' @{id='offline-changed';name='Changed Global Settings';token='changed-token';authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl='http://127.0.0.1:9/v1';models=@('changed-model')};image=@{enabled=$false;protocol='openai-images';baseUrl='';models=@()}}|Out-Null
    Api $connection '/api/settings' 'POST' @{providerId='offline-schedule';model='offline-model';effort='low';maxTurns=140;permissionMode='readonly';workspace=$root;allowedTools='';disallowedTools=''}|Out-Null
    $successId='schedule-success-'+[guid]::NewGuid().ToString('N');$due=[DateTimeOffset]::UtcNow.AddSeconds(2).ToString('o')
    $scheduledSession=[guid]::NewGuid().ToString();Api $connection '/api/workbench/schedules' 'POST' @(@{id=$successId;sessionId=$scheduledSession;enabled=$true;at=$due;repeatMinutes=0;maxRetries=0;conflictPolicy='queue';text='schedule-success';workspace=$root;providerId='offline-schedule';providerName='Offline Schedule';model='offline-model';effort='low';maxTurns=137;permissionMode='readonly';allowedTools=@('Read');disallowedTools=@('Bash')})|Out-Null
    $deleteResponse=Invoke-WebRequest -UseBasicParsing -SkipHttpErrorCheck -Uri ($connection.Base+"/api/sessions/$scheduledSession") -Headers $connection.Headers -Method DELETE -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes((@{workspace=$root;transcriptIds=@()}|ConvertTo-Json -Compress)))
    if([int]$deleteResponse.StatusCode-ne409-or-not([string]$deleteResponse.Content).Contains('session_has_background_work')){throw 'Scheduled session could be permanently deleted before execution'}
    Api $connection '/api/settings' 'POST' @{providerId='offline-changed';model='changed-model';effort='max';maxTurns=222;permissionMode='agent';workspace=$root;allowedTools='Bash';disallowedTools='Read'}|Out-Null
    $success=Wait-Schedule $connection $successId 'completed'
    if([string]::IsNullOrWhiteSpace([string]$success.lastRunId)-or-not[string]::IsNullOrWhiteSpace([string]$success.activeRunId)){throw 'Successful scheduled Run did not retain terminal Run linkage'}
    $successRequest=Get-Content -LiteralPath (Join-Path $root ".claude-gui-v2\runs\$($success.lastRunId)\request.json") -Raw -Encoding UTF8|ConvertFrom-Json
    if([string]$successRequest.provider.id-ne'offline-schedule'-or[string]$successRequest.model-ne'offline-model'-or[int]$successRequest.maxTurns-ne137-or[string]$successRequest.permissionMode-ne'readonly'){throw ('Scheduled Run drifted to later global Provider/model/permission settings: '+($successRequest|ConvertTo-Json -Depth 8 -Compress))}
    $failedId='schedule-failure-'+[guid]::NewGuid().ToString('N')
    Api $connection '/api/workbench/schedules' 'POST' @(@{id=$failedId;sessionId=[guid]::NewGuid().ToString();enabled=$true;at=[DateTimeOffset]::UtcNow.AddSeconds(-2).ToString('o');repeatMinutes=0;maxRetries=0;conflictPolicy='queue';text='provider-auth-failure';workspace=$root;providerId='offline-schedule';providerName='Offline Schedule';model='offline-model';effort='low';maxTurns=137;permissionMode='readonly';allowedTools=@('Read');disallowedTools=@('Bash')})|Out-Null
    $failed=Wait-Schedule $connection $failedId 'dead_letter'
    if([string]::IsNullOrWhiteSpace([string]$failed.lastRunId)-or([string]$failed.lastError).Contains('sk-provider-health-secret-value')){throw 'Failed scheduled Run did not enter failure box with redacted error'}
    $visual='SKIPPED'
    if($visualArguments.Count -eq 4){
        foreach($path in @($NodeExecutable,$PlaywrightRoot,$BrowserExecutable)){if(-not(Test-Path -LiteralPath $path)){throw "Visual dependency not found: $path"}}
        [IO.Directory]::CreateDirectory($VisualOutputDirectory)|Out-Null
        $env:NODE_PATH=(Resolve-Path -LiteralPath $PlaywrightRoot).Path
        $env:CLAUDE_UI_BASE_URL=$connection.Base
        $env:CLAUDE_UI_SECRET=$secret
        $env:CLAUDE_UI_BROWSER_EXE=(Resolve-Path -LiteralPath $BrowserExecutable).Path
        $env:CLAUDE_UI_OUTPUT_DIR=(Resolve-Path -LiteralPath $VisualOutputDirectory).Path
        & (Resolve-Path -LiteralPath $NodeExecutable).Path (Join-Path $PSScriptRoot 'schedule-evidence-ui-visual.js')
        if($LASTEXITCODE -ne 0){throw "Schedule evidence visual test failed: $LASTEXITCODE"}
        $visual='PASS'
    }
    [pscustomobject]@{ScheduleRunTracking='PASS';ScheduleSnapshotIsolation='PASS';ScheduledDeletionGuard='PASS';ScheduleEvidenceVisual=$visual;SuccessfulRun=$success.lastRunId;SuccessfulState=$success.state;FailedRun=$failed.lastRunId;FailedState=$failed.state;SecretLeaked=$false;Workspace=$root}|Format-List
}
finally { Stop-TestUi $root $Executable;if($process-and-not$process.HasExited){Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue};Get-CimInstance Win32_Process|Where-Object{$_.ExecutablePath-eq$fake}|ForEach-Object{Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue};Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_CLAUDE_EXE,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE,Env:NODE_PATH,Env:CLAUDE_UI_BASE_URL,Env:CLAUDE_UI_SECRET,Env:CLAUDE_UI_BROWSER_EXE,Env:CLAUDE_UI_OUTPUT_DIR -ErrorAction SilentlyContinue }

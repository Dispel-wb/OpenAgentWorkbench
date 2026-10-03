param(
    [Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$NodePath,[Parameter(Mandatory=$true)][string]$ClaudePath,[Parameter(Mandatory=$true)][string]$CodexPath,[Parameter(Mandatory=$true)][string]$DshEntry,[Parameter(Mandatory=$true)][string]$PiEntry,[Parameter(Mandatory=$true)][string]$ProgressPath,[double]$Hours=72,[int]$CycleIntervalSeconds=300
)
$ErrorActionPreference='Stop';$ProgressPath=[IO.Path]::GetFullPath($ProgressPath)
if(Test-Path -LiteralPath $ProgressPath){throw 'Use a new progress path; previous evidence is immutable'}
$CodexPath=(Resolve-Path -LiteralPath $CodexPath).Path
$frozenRoot=$ProgressPath+'.frozen'
if(Test-Path -LiteralPath $frozenRoot){throw "Frozen input directory already exists: $frozenRoot"}
[IO.Directory]::CreateDirectory($frozenRoot)|Out-Null
$sourceHash=(Get-FileHash -LiteralPath $CodexPath -Algorithm SHA256).Hash
$frozenCodex=Join-Path $frozenRoot 'codex.exe';$copyTemp=$frozenCodex+'.tmp'
[IO.File]::Copy($CodexPath,$copyTemp,$false)
[IO.File]::Move($copyTemp,$frozenCodex)
$frozenHash=(Get-FileHash -LiteralPath $frozenCodex -Algorithm SHA256).Hash
$sourceHashAfter=(Get-FileHash -LiteralPath $CodexPath -Algorithm SHA256).Hash
if($sourceHash-ne$frozenHash-or$sourceHashAfter-ne$sourceHash){throw 'Codex CLI changed while creating the frozen validation copy'}
[IO.File]::WriteAllText((Join-Path $frozenRoot 'manifest.json'),([ordered]@{schemaVersion=1;createdAt=[DateTimeOffset]::Now.ToString('o');sourcePath=$CodexPath;sourceSha256=$sourceHash;frozenPath=$frozenCodex;frozenSha256=$frozenHash}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$runner=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'multi-kernel-soak.ps1')).Path
$hash=[Security.Cryptography.SHA256]::Create();try{$id=-join($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($ProgressPath.ToLowerInvariant()))|ForEach-Object{$_.ToString('x2')})}finally{$hash.Dispose()}
$task='OpenAgentWorkbench-MultiKernel-'+$id.Substring(0,12);Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
function Q([string]$v){'"'+$v.Replace('"','\"')+'"'}
$args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File '+(Q $runner)+' -Executable '+(Q ([IO.Path]::GetFullPath($Executable)))+' -NodePath '+(Q ([IO.Path]::GetFullPath($NodePath)))+' -ClaudePath '+(Q ([IO.Path]::GetFullPath($ClaudePath)))+' -CodexPath '+(Q $frozenCodex)+' -DshEntry '+(Q ([IO.Path]::GetFullPath($DshEntry)))+' -PiEntry '+(Q ([IO.Path]::GetFullPath($PiEntry)))+' -ProgressPath '+(Q $ProgressPath)+' -Hours '+$Hours.ToString([Globalization.CultureInfo]::InvariantCulture)+' -CycleIntervalSeconds '+$CycleIntervalSeconds
$pwsh=(Get-Command pwsh -ErrorAction Stop).Source
$action=New-ScheduledTaskAction -Execute $pwsh -Argument $args -WorkingDirectory $PSScriptRoot
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::FromHours($Hours*2+6)) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $task -Action $action -Settings $settings -Description 'Open Agent Workbench 72h parallel Claude/Codex/DSHarness/Pi validation'|Out-Null;Start-ScheduledTask -TaskName $task
$until=(Get-Date).AddSeconds(30);do{Start-Sleep -Milliseconds 250;if(Test-Path $ProgressPath){$progress=Get-Content $ProgressPath -Raw|ConvertFrom-Json;if($progress.monitorPid){$progress|Add-Member -NotePropertyName scheduledTask -NotePropertyValue $task -Force;$progress|ConvertTo-Json -Depth 8;exit 0}}}while((Get-Date)-lt$until)
throw "Scheduled multi-kernel soak did not publish progress: $task"

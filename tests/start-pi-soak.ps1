param(
    [Parameter(Mandatory=$true)][string]$ProgressPath,
    [ValidateRange(0.001,168)][double]$Hours=72,
    [ValidateRange(1,3600)][int]$CycleIntervalSeconds=300,
    [switch]$Run
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$manifestPath=Join-Path $root 'input-manifest.json'
if(-not(Test-Path -LiteralPath $manifestPath)){throw 'Run this launcher from a frozen Pi soak directory'}
$ProgressPath=[IO.Path]::GetFullPath($ProgressPath)
if($ProgressPath.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Keep reports outside the frozen inputs directory'}
$sha=[Security.Cryptography.SHA256]::Create()
try{$id=-join($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ProgressPath.ToLowerInvariant()))|ForEach-Object{$_.ToString('x2')})}finally{$sha.Dispose()}
$taskName='OpenAgentWorkbench-PiSoak-'+$id.Substring(0,12)
function Assert-FrozenInputs {
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    if($manifest.schemaVersion-ne1-or@($manifest.files).Count-lt4){throw 'Invalid frozen input manifest'}
    foreach($file in $manifest.files){
        $path=[IO.Path]::GetFullPath((Join-Path $root $file.path))
        if(-not$path.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Frozen manifest path escapes its root'}
        if((Get-Item -LiteralPath $path).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Frozen inputs may not be linked'}
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash-ne$file.sha256){throw "Frozen input changed: $($file.path)"}
    }
    if((Get-FileHash -LiteralPath (Join-Path $root 'OpenAgentWorkbench.exe') -Algorithm SHA256).Hash-ne$manifest.candidateSha256){throw 'Frozen candidate hash mismatch'}
}
if($Run){
    try{
        $manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        Assert-FrozenInputs
        & (Join-Path $PSScriptRoot 'pi-host-integration.ps1') -Executable (Join-Path $root 'OpenAgentWorkbench.exe') -PiEntry (Join-Path $root 'runtimes\pi\node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js') -NodePath (Join-Path $root 'tools\node.exe') -SoakHours $Hours -CycleIntervalSeconds $CycleIntervalSeconds -ProgressPath $ProgressPath
        Assert-FrozenInputs
        if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash-ne$manifestHash){throw 'Frozen manifest changed during validation'}
        $progress=Get-Content -LiteralPath $ProgressPath -Raw|ConvertFrom-Json
        $progress|Add-Member -NotePropertyName frozenInputsValidated -NotePropertyValue $true -Force
        $progress|Add-Member -NotePropertyName frozenManifestSha256 -NotePropertyValue $manifestHash -Force
        [IO.File]::WriteAllText($ProgressPath,($progress|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    }catch{
        $failure=$_
        $progress=if(Test-Path -LiteralPath $ProgressPath){Get-Content -LiteralPath $ProgressPath -Raw|ConvertFrom-Json}else{[pscustomobject]@{state='failed';message='';completedAt=''}}
        $progress.state='failed';$progress.message=$failure.Exception.Message;$progress.completedAt=[DateTimeOffset]::UtcNow.ToString('o')
        $progress|Add-Member -NotePropertyName frozenInputsValidated -NotePropertyValue $false -Force
        [IO.Directory]::CreateDirectory((Split-Path $ProgressPath -Parent))|Out-Null
        [IO.File]::WriteAllText($ProgressPath,($progress|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
        throw $failure
    }finally{Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue}
    return
}
if(Test-Path -LiteralPath $ProgressPath){throw 'Use a new progress path; previous validation evidence exists'}
if(Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue){throw 'A task already owns this progress path'}
$pwsh=(Get-Command pwsh -ErrorAction Stop).Source
$scriptPath=(Resolve-Path -LiteralPath $MyInvocation.MyCommand.Path).Path
$arguments='-NoProfile -NonInteractive -WindowStyle Hidden -File "'+$scriptPath+'" -Run -ProgressPath "'+$ProgressPath+'" -Hours '+$Hours.ToString([Globalization.CultureInfo]::InvariantCulture)+' -CycleIntervalSeconds '+$CycleIntervalSeconds
$action=New-ScheduledTaskAction -Execute $pwsh -Argument $arguments -WorkingDirectory $root
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromHours($Hours*2+6)) -MultipleInstances IgnoreNew
# Demand-only task: no later trigger can accidentally rerun a completed short validation.
Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -Description 'Isolated same-Host Pi workload validation; loopback model only'|Out-Null
Start-ScheduledTask -TaskName $taskName
[pscustomobject]@{PiSoak='START_REQUESTED';ScheduledTask=$taskName;ProgressPath=$ProgressPath;FrozenRoot=$root;Hours=$Hours}|ConvertTo-Json -Compress

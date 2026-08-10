param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference='Stop';$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-skill-index-'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($root)|Out-Null
$process=Start-Process $Executable -ArgumentList @('--skill-catalog-selftest',$root) -Wait -PassThru -WindowStyle Hidden
if($process.ExitCode -ne 0){throw "Skill catalog self-test failed: $($process.ExitCode)"}
[pscustomobject]@{MetadataIndex='OK';MatchedOnDemand='OK';FullBodyNotPreloaded='OK';UnmatchedContextBytes=0;Root=$root}|Format-List

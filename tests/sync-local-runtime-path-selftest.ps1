$ErrorActionPreference='Stop'
$sourceRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('sync-local-path-'+[guid]::NewGuid().ToString('N'))
$installRoot=Join-Path $testRoot 'install'
$externalRoot=Join-Path $testRoot 'external-runtimes'
$workspace=Join-Path $testRoot 'workspace'
$candidate=Join-Path $testRoot 'candidate.exe'
$junction=Join-Path $installRoot 'runtimes'
try {
  [IO.Directory]::CreateDirectory($installRoot)|Out-Null
  [IO.Directory]::CreateDirectory($externalRoot)|Out-Null
  foreach($runtime in @('pi','dsh')){
    $runtimeSource=Join-Path $workspace ('runtimes\'+$runtime)
    [IO.Directory]::CreateDirectory($runtimeSource)|Out-Null
    [IO.File]::WriteAllText((Join-Path $runtimeSource 'package.json'),'{}',[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $runtimeSource 'package-lock.json'),'{"lockfileVersion":3}',[Text.UTF8Encoding]::new($false))
  }
  [IO.File]::WriteAllBytes($candidate,[byte[]](1,2,3))
  New-Item -ItemType Junction -Path $junction -Target $externalRoot|Out-Null
  $rejected=$false;$message=''
  try {
    & (Join-Path $sourceRoot 'sync_local_version.ps1') -Executable $candidate -InstallRoot $installRoot -WorkspaceRoot $workspace
  } catch {
    $message=$_.Exception.Message
    $rejected=$message -match 'reparse point'
  }
  if(-not$rejected){throw "Parent runtime junction was not rejected: $message"}
  if(Test-Path -LiteralPath (Join-Path $externalRoot 'pi\package.json')){throw 'Runtime files escaped through the parent junction before rejection'}
  [pscustomobject]@{SyncRuntimePathPolicy='PASS';ParentJunctionRejected=$true;ExternalWritePrevented=$true}
} finally {
  if(Test-Path -LiteralPath $junction){Remove-Item -LiteralPath $junction -Force -ErrorAction SilentlyContinue}
  $resolved=[IO.Path]::GetFullPath($testRoot);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
  if((Test-Path -LiteralPath $resolved)-and$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue}
}

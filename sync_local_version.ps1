param(
  [Parameter(Mandatory=$true)][string]$Executable,
  [string]$InstallRoot = 'D:\softwares\ClaudeCode',
  [string]$WorkspaceRoot = '',
  [int]$Keep = 5,
  [string]$TargetName = 'ClaudeCodeWorkbench.exe'
)
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $Executable).Path
$targetRoot=(Resolve-Path -LiteralPath $InstallRoot).Path
$target=Join-Path $targetRoot $TargetName
# Keep external agent runtimes on the same dependency lock as the executable source.
# A lock marker avoids reinstalling thousands of files when nothing changed.
if($WorkspaceRoot){
  $workspace=(Resolve-Path -LiteralPath $WorkspaceRoot).Path
  $npm=(Get-Command npm.cmd -ErrorAction SilentlyContinue)
  if(-not $npm){ $npm=(Get-Command npm -ErrorAction Stop) }
  foreach($runtimeName in @('pi','dsh')){
    $runtimeSource=Join-Path $workspace ('runtimes\'+$runtimeName)
    $runtimeTarget=Join-Path $targetRoot ('runtimes\'+$runtimeName)
    $lockSource=Join-Path $runtimeSource 'package-lock.json'
    $manifestSource=Join-Path $runtimeSource 'package.json'
    if(-not(Test-Path -LiteralPath $lockSource) -or -not(Test-Path -LiteralPath $manifestSource)){ throw "Missing locked runtime source: $runtimeSource" }
    [IO.Directory]::CreateDirectory($runtimeTarget)|Out-Null
    $resolvedRuntimeTarget=(Resolve-Path -LiteralPath $runtimeTarget).Path
    $runtimeRoot=[IO.Path]::GetFullPath((Join-Path $targetRoot 'runtimes'))+[IO.Path]::DirectorySeparatorChar
    if(-not $resolvedRuntimeTarget.StartsWith($runtimeRoot,[StringComparison]::OrdinalIgnoreCase)){ throw "Unsafe runtime target: $resolvedRuntimeTarget" }
    if(((Get-Item -LiteralPath $resolvedRuntimeTarget -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){ throw "Runtime target cannot be a reparse point: $resolvedRuntimeTarget" }
    $lockHash=(Get-FileHash -LiteralPath $lockSource -Algorithm SHA256).Hash
    $marker=Join-Path $runtimeTarget '.workbench-lock.sha256'
    $installedHash=if(Test-Path -LiteralPath $marker){(Get-Content -LiteralPath $marker -Raw).Trim()}else{''}
    if($installedHash -ne $lockHash){
      Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $runtimeTarget 'package.json') -Force
      Copy-Item -LiteralPath $lockSource -Destination (Join-Path $runtimeTarget 'package-lock.json') -Force
      & $npm.Source ci --omit=dev --ignore-scripts --no-audit --no-fund --prefix $runtimeTarget
      if($LASTEXITCODE -ne 0){ throw "Failed to synchronize $runtimeName runtime" }
      [IO.File]::WriteAllText($marker,$lockHash+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
    }
  }
}
# Installed Local is synchronized in place; installed backup versions are not retained.
Copy-Item -LiteralPath $source -Destination $target -Force
# The installation root keeps only the canonical latest executable on its top level.
Get-ChildItem -LiteralPath $targetRoot -File | Where-Object {
  (
    $_.Name -like 'ClaudeCodeWorkbench-*.exe' -or
    $_.Name -like 'OpenAgentWorkbench-*.exe' -or
    $_.Name -like 'ClaudeCodeWorkbench.exe.bak-*'
  ) -and $_.FullName -ne $target
} | Remove-Item -Force
if($WorkspaceRoot){
  $dist=Join-Path $workspace 'dist'
  if(Test-Path -LiteralPath $dist){
    $distRoot=[IO.Path]::GetFullPath($dist)
    $files=@(Get-ChildItem -LiteralPath $dist -Recurse -File | Where-Object {
      ($_.Name -like 'ClaudeCodeWorkbench-*.exe' -or $_.Name -like 'OpenAgentWorkbench-*.exe') -and
      $_.Name -notin @('ClaudeCodeWorkbench.exe','OpenAgentWorkbench.exe')
    }) | Sort-Object LastWriteTime -Descending
    foreach($old in @($files | Select-Object -Skip ([Math]::Max(0,$Keep)))){
      $oldPath=[IO.Path]::GetFullPath($old.FullName)
      if(-not $oldPath.StartsWith($distRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){ throw "Unsafe workspace cleanup path: $oldPath" }
      Remove-Item -LiteralPath $oldPath -Force
    }
    $kept=@($files | Select-Object -First $Keep | ForEach-Object FullName)
  } else { $kept=@() }
} else { $kept=@() }
[pscustomobject]@{synced=$target; installedBackups=0; workspaceKept=$kept} | ConvertTo-Json -Depth 3



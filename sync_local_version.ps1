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
  $workspace=(Resolve-Path -LiteralPath $WorkspaceRoot).Path
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



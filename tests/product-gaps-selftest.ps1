param([string]$DependencyRoot=(Join-Path $PSScriptRoot '..\.packages'))
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
$compiler=Join-Path $DependencyRoot 'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe'
$json=Join-Path $DependencyRoot 'newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('workbench-boundary-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
try {
  $exe=Join-Path $scratch 'test.exe'
  & $compiler /nologo /target:exe /langversion:latest "/out:$exe" "/reference:$json" /reference:System.Xml.dll /reference:System.Xml.Linq.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot 'product-gaps-selftest.cs') (Join-Path $sourceRoot 'native\ExtensionPackagePolicy.cs') (Join-Path $sourceRoot 'native\DocumentPreview.cs') (Join-Path $sourceRoot 'native\OfficeMedia.cs') (Join-Path $sourceRoot 'native\McpRuntimePolicy.cs') (Join-Path $sourceRoot 'native\WorkflowDag.cs') (Join-Path $sourceRoot 'native\HttpQuery.cs')
  if($LASTEXITCODE-ne 0){throw 'Boundary test compilation failed'}
  Copy-Item -LiteralPath $json -Destination (Join-Path $scratch 'Newtonsoft.Json.dll')
  & $exe
  if($LASTEXITCODE-ne 0){throw 'Boundary test failed'}
} finally {
  $resolved=[IO.Path]::GetFullPath($scratch)
  if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and [IO.Path]::GetFileName($resolved).StartsWith('workbench-boundary-test-')){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

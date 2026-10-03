param([string]$DependencyRoot='')
$ErrorActionPreference='Stop'
$DependencyRoot=if($DependencyRoot){$DependencyRoot}else{Join-Path $PSScriptRoot '..\.packages'}
$compiler=Join-Path $DependencyRoot 'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe'
$json=Join-Path $DependencyRoot 'newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll'
$sourceRoot=Split-Path $PSScriptRoot -Parent
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('durable-process-identity-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
try {
  $exe=Join-Path $scratch 'test.exe'
  & $compiler /nologo /target:exe /platform:x64 /langversion:latest "/out:$exe" "/reference:$json" (Join-Path $PSScriptRoot 'durable-process-identity-selftest.cs') (Join-Path $sourceRoot 'native\DurableProcessIdentity.cs')
  if($LASTEXITCODE-ne0){throw 'Durable process identity self-test compilation failed'}
  Copy-Item -LiteralPath $json -Destination (Join-Path $scratch 'Newtonsoft.Json.dll')
  & $exe
  if($LASTEXITCODE-ne0){throw "Durable process identity self-test failed: $LASTEXITCODE"}
} finally {
  Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

param([string]$DependencyRoot=(Join-Path $PSScriptRoot '..\.packages'))
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
$compiler=Join-Path $DependencyRoot 'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('sdk-frame-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
try {
    $exe=Join-Path $scratch 'test.exe'
    & $compiler /nologo /target:exe /langversion:latest "/out:$exe" (Join-Path $PSScriptRoot 'sdk-frame-reader-selftest.cs') (Join-Path $sourceRoot 'native\SdkFrameReader.cs')
    if($LASTEXITCODE-ne 0){throw 'Frame reader compilation failed'}
    & $exe
    if($LASTEXITCODE-ne 0){throw 'Frame reader test failed'}
} finally {
    $resolved=[IO.Path]::GetFullPath($scratch)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and [IO.Path]::GetFileName($resolved).StartsWith('sdk-frame-test-')){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

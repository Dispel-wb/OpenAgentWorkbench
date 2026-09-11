param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$dependencies=& (Join-Path $PSScriptRoot 'resolve-test-build-dependencies.ps1')
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('terminal-poll-race-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
$testExe=Join-Path $scratch 'test.exe'
& $dependencies.Compiler /nologo /target:exe /platform:x64 /langversion:latest "/out:$testExe" "/reference:$($dependencies.Json)" /reference:System.Net.Http.dll (Join-Path $PSScriptRoot 'terminal-poll-race-selftest.cs')
if($LASTEXITCODE-ne0){throw 'Terminal poll race test compilation failed'}
Copy-Item -LiteralPath $dependencies.Json -Destination (Join-Path $scratch 'Newtonsoft.Json.dll')
& $testExe $Executable $scratch
if($LASTEXITCODE-ne0){throw "Terminal poll race failed; evidence retained at $scratch"}

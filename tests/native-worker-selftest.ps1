param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('claude-native-worker-' + [guid]::NewGuid().ToString('N'))
$fake = Join-Path $root 'fake-claude.exe'
[IO.Directory]::CreateDirectory($root) | Out-Null
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
$json = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll'
$source = Join-Path $PSScriptRoot 'fake-claude-worker.cs'
& $csc /nologo /target:exe /platform:x64 "/out:$fake" "/reference:$json" $source
if ($LASTEXITCODE -ne 0) { throw "Fake worker build failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $json -Destination (Join-Path $root 'Newtonsoft.Json.dll')
$process = Start-Process -FilePath $Executable -ArgumentList @('--native-worker-selftest', $fake, $root) -Wait -PassThru
$code = $process.ExitCode
if ($code -ne 0) {
    $detail = Join-Path $root 'selftest-error.txt'
    if (Test-Path -LiteralPath $detail) { Get-Content -LiteralPath $detail -Raw -Encoding UTF8 }
    throw "Native Worker self-test failed: $code"
}
[pscustomobject]@{ NativeWorker = 'OK'; Utf8 = 'OK'; MultiTurn = 'OK'; CompletedInputDedupe = 'OK'; InFlightRecovery = 'OK'; PauseResume = 'OK'; JobObjectStop = 'OK'; Root = $root } | Format-List

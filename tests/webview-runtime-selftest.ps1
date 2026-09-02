param([string]$VisualStudioRoot = '', [string]$DependencyRoot = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vsRoot = $VisualStudioRoot
if ($DependencyRoot) { $csc = Join-Path ([IO.Path]::GetFullPath($DependencyRoot)) 'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe' }
else {
    if (-not $vsRoot) { $vsRoot = $env:VSINSTALLDIR }
    if (-not $vsRoot) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) { $vsRoot = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1) }
    }
    if (-not $vsRoot) { throw 'Visual Studio with Roslyn was not found.' }
    $csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw "Roslyn compiler not found: $csc" }
$output = Join-Path ([IO.Path]::GetTempPath()) ('webview-runtime-selftest-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    & $csc /nologo /target:exe /platform:x64 "/out:$output" (Join-Path $root 'native\WebViewRuntimeInfo.cs') (Join-Path $PSScriptRoot 'webview-runtime-selftest.cs')
    if ($LASTEXITCODE -ne 0) { throw "WebView runtime self-test build failed: $LASTEXITCODE" }
    & $output
    if ($LASTEXITCODE -ne 0) { throw "WebView runtime self-test failed: $LASTEXITCODE" }
} finally {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
}

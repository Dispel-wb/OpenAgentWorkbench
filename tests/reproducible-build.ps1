param(
    [ValidateSet('Local','OpenSource')][string]$Edition = 'OpenSource',
    [Parameter(Mandatory = $true)][string]$DependencyRoot,
    [Parameter(Mandatory = $true)][string]$PythonPath,
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('oaw-repro-' + [guid]::NewGuid().ToString('N'))
$report = if ($ReportPath) { [IO.Path]::GetFullPath($ReportPath) } else { Join-Path $PSScriptRoot 'reproducible-build-result.json' }
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $name = if ($Edition -eq 'OpenSource') { 'OpenAgentWorkbench.exe' } else { 'ClaudeCodeWorkbench.exe' }
    $first = Join-Path $temporary (Join-Path 'first' $name)
    $second = Join-Path $temporary (Join-Path 'second' $name)
    & (Join-Path $root 'build_exe.ps1') -Edition $Edition -DependencyRoot $DependencyRoot -PythonPath $PythonPath -OutputPath $first
    if ($LASTEXITCODE -ne 0) { throw "First build failed: $LASTEXITCODE" }
    & (Join-Path $root 'build_exe.ps1') -Edition $Edition -DependencyRoot $DependencyRoot -PythonPath $PythonPath -OutputPath $second
    if ($LASTEXITCODE -ne 0) { throw "Second build failed: $LASTEXITCODE" }
    $firstHash = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash
    $result = [ordered]@{
        schemaVersion = 1
        state = if ($firstHash -eq $secondHash) { 'passed' } else { 'failed' }
        edition = $Edition
        sha256 = $firstHash
        comparisonSha256 = $secondHash
        size = (Get-Item -LiteralPath $first).Length
        dependenciesLockSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'build\dependencies.lock.json') -Algorithm SHA256).Hash
        verifiedAt = [DateTimeOffset]::Now.ToString('o')
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $report)) | Out-Null
    [IO.File]::WriteAllText($report, ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    if ($firstHash -ne $secondHash) { throw "Builds are not reproducible: $firstHash != $secondHash" }
    [pscustomobject]$result | Format-List
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ((Test-Path -LiteralPath $resolved) -and $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

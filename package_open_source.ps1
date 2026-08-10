param(
    [string]$Destination = '',
    [string]$Version = '0.1.0-preview.1'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$resolvedSource = (Resolve-Path -LiteralPath $sourceRoot).Path
$defaultDestination = Join-Path (Split-Path -Parent (Split-Path -Parent $resolvedSource)) 'outputs\OpenAgentWorkbench-public'
$destinationPath = if ($Destination) { [IO.Path]::GetFullPath($Destination) } else { [IO.Path]::GetFullPath($defaultDestination) }

if ($destinationPath -eq $resolvedSource -or $destinationPath.StartsWith($resolvedSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Public staging directory must be outside the source directory.'
}

$allowedParent = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent (Split-Path -Parent $resolvedSource)) 'outputs'))
if (-not $destinationPath.StartsWith($allowedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace a staging directory outside $allowedParent"
}

if (Test-Path -LiteralPath $destinationPath) {
    Remove-Item -LiteralPath $destinationPath -Recurse -Force
}
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null

function Copy-PublicFile {
    param([string]$RelativePath)
    $from = Join-Path $resolvedSource $RelativePath
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing public source file: $RelativePath" }
    $to = Join-Path $destinationPath $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to -Force
}

foreach ($file in @('.gitignore','LICENSE','README.md','SECURITY.md','THIRD_PARTY_NOTICES.md','build_exe.ps1','package_open_source.ps1')) {
    Copy-PublicFile $file
}
Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'third_party') -File | ForEach-Object {
    Copy-PublicFile (Join-Path 'third_party' $_.Name)
}

Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'native') -File | Where-Object { $_.Extension -eq '.cs' -or $_.Name -eq 'app.manifest' } | ForEach-Object {
    Copy-PublicFile (Join-Path 'native' $_.Name)
}

foreach ($file in @('static\index.html','static\app.js','static\styles.css')) { Copy-PublicFile $file }
Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'static\vendor') -File | ForEach-Object {
    Copy-PublicFile (Join-Path 'static\vendor' $_.Name)
}

$publicTests = @(
    'edition-smoke.ps1',
    'event-store-selftest.ps1',
    'fake-claude-worker.cs',
    'native-agent-fault-stress.ps1',
    'native-agent-integration.ps1',
    'native-diagnostics-selftest.ps1',
    'native-installer-selftest.ps1',
    'native-m2-integration.ps1',
    'native-m3-queue-integration.ps1',
    'native-soak.ps1',
    'native-updater-selftest.ps1',
    'native-worker-selftest.ps1',
    'native_adapter_smoke.js',
    'native_offline_smoke.js',
    'permission-demo.cs',
    'process-split-native.ps1',
    'skill-catalog-selftest.ps1',
    'smoke-native.ps1',
    'task-security-selftest.ps1',
    'task-workspace-selftest.ps1',
    'ui-contract-selftest.js',
    'ui-host-reconnect.ps1',
    'ui-resilience.ps1'
)
foreach ($file in $publicTests) {
    $candidate = Join-Path $resolvedSource (Join-Path 'tests' $file)
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { Copy-PublicFile (Join-Path 'tests' $file) }
}

$manifest = [ordered]@{
    product = 'Open Agent Workbench'
    release = $Version
    edition = 'opensource'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    sourceFiles = @(Get-ChildItem -LiteralPath $destinationPath -Recurse -File | ForEach-Object { $_.FullName.Substring($destinationPath.Length + 1).Replace('\','/') } | Sort-Object)
    exclusions = @('local artwork','screenshots','API credentials','runtime data','transcripts','databases','logs','diagnostic bundles','legacy binaries')
}
$manifestPath = Join-Path $destinationPath 'PUBLIC_RELEASE_MANIFEST.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

Write-Output $destinationPath

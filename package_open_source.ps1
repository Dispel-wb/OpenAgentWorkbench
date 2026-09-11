param(
    [string]$Destination = '',
    [string]$Version = '1.0.0'
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
    Get-ChildItem -LiteralPath $destinationPath -Force | Where-Object { $_.Name -ne '.git' } | ForEach-Object {
        $candidate = [IO.Path]::GetFullPath($_.FullName)
        if (-not $candidate.StartsWith($destinationPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe staging cleanup path: $candidate" }
        Remove-Item -LiteralPath $candidate -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null

function Copy-PublicFile {
    param([string]$RelativePath)
    $from = Join-Path $resolvedSource $RelativePath
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw "Missing public source file: $RelativePath" }
    $to = Join-Path $destinationPath $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
    # Public sources are text-only; canonical line endings match .gitattributes in every checkout.
    $content = [IO.File]::ReadAllText($from).Replace("`r`n", "`n")
    [IO.File]::WriteAllText($to, $content, [Text.UTF8Encoding]::new($false))
}

foreach ($file in @('.gitignore','LICENSE','README.md','SECURITY.md','THIRD_PARTY_NOTICES.md','CONTRIBUTING.md','CODE_OF_CONDUCT.md','CHANGELOG.md','GOVERNANCE.md','build_exe.ps1','package_open_source.ps1','restore_build_dependencies.ps1','install_dsh_runtime.ps1','runtimes\dsh\package.json','runtimes\dsh\package-lock.json')) {
    Copy-PublicFile $file
}
Copy-PublicFile '.gitattributes'
Copy-PublicFile 'docs\WINDOWS_SANDBOX_PLAN.md'
# Evidence must be explicitly reviewed and sanitized before placing it here.
$evidenceRoot = Join-Path $resolvedSource 'release-evidence'
if (Test-Path -LiteralPath $evidenceRoot -PathType Container) {
    Get-ChildItem -LiteralPath $evidenceRoot -Directory | Where-Object { $_.Name -match '^[0-9]+\.[0-9]+\.[0-9]+(\+[0-9A-Za-z.-]+)?$' } | ForEach-Object {
        foreach ($name in @('readiness','soak','windows','native-performance','accessibility','security-review','regression')) {
            Copy-PublicFile (Join-Path "release-evidence\$($_.Name)" "$name.json")
        }
    }
}
foreach ($file in @('install_pi_runtime.ps1','runtimes\pi\package.json','runtimes\pi\package-lock.json','docs\PI_AGENT.md')) { Copy-PublicFile $file }
Copy-PublicFile 'build\dependencies.lock.json'
Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'third_party') -File | ForEach-Object {
    Copy-PublicFile (Join-Path 'third_party' $_.Name)
}

Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'native') -File | Where-Object { $_.Extension -eq '.cs' -or $_.Name -eq 'app.manifest' } | ForEach-Object {
    Copy-PublicFile (Join-Path 'native' $_.Name)
}

foreach ($file in @('static\index.html','static\app.js','static\styles.css')) { Copy-PublicFile $file }
Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'static\modules') -File -Filter '*.js' | ForEach-Object {
    Copy-PublicFile (Join-Path 'static\modules' $_.Name)
}
Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'static\vendor') -File | ForEach-Object {
    Copy-PublicFile (Join-Path 'static\vendor' $_.Name)
}

Get-ChildItem -LiteralPath (Join-Path $resolvedSource 'tests') -File | Where-Object {
    $_.Extension -in @('.ps1','.js','.cs','.md') -and $_.Name -notmatch '(?i)(result|debug|runner-error|stdout|stderr)'
} | ForEach-Object { Copy-PublicFile (Join-Path 'tests' $_.Name) }

foreach ($document in @('THREAT_MODEL.md','AGENT_WORKER_SDK.md','ARCHITECTURE.md','DEVELOPMENT.md','DATA_MODEL.md','BUILD_REPRODUCIBILITY.md','WINDOWS_TEST_MATRIX.md','RELEASE_PROCESS.md','VALIDATION_STATUS.md','VALIDATION_6.4.24.md','VALIDATION_PREVIEW_CANCELLATION.md','MATURITY_GAPS.md','V1_RELEASE_READINESS.md')) {
    Copy-PublicFile (Join-Path 'docs' $document)
}

Get-ChildItem -LiteralPath (Join-Path $resolvedSource '.github') -Recurse -File | ForEach-Object {
    Copy-PublicFile $_.FullName.Substring($resolvedSource.Length + 1)
}

$manifest = [ordered]@{
    product = 'Open Agent Workbench'
    release = $Version
    edition = 'opensource'
    sourceDateEpoch = if ($env:SOURCE_DATE_EPOCH) { $env:SOURCE_DATE_EPOCH } else { 'not-set' }
    sourceFiles = @(Get-ChildItem -LiteralPath $destinationPath -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } | ForEach-Object { $_.FullName.Substring($destinationPath.Length + 1).Replace('\','/') } | Sort-Object)
    exclusions = @('local artwork','screenshots','API credentials','runtime data','transcripts','databases','logs','diagnostic bundles','legacy binaries')
}
$manifestPath = Join-Path $destinationPath 'PUBLIC_RELEASE_MANIFEST.json'
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

Write-Output $destinationPath

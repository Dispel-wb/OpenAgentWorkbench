param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateSet('auto','windows-10','windows-11')][string]$ExpectedWindows = 'auto',
    [string]$WebViewRuntime = 'evergreen',
    [ValidateSet('any','admin','non-admin')][string]$ExpectedPrivilege = 'any',
    [ValidateSet('local','opensource')][string]$ExpectedEdition = 'local',
    [switch]$RequireClaudeAbsent,
    [switch]$RequireNonDDrive,
    [switch]$RequireUnicodeUserProfile,
    [switch]$Full,
    [string]$DependencyRoot = '',
    [string]$NodePath = '',
    [string]$ResultsPath = ''
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$executablePath = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) { throw "Executable not found: $executablePath" }

function Get-WindowsIdentityEvidence {
    $os = Get-CimInstance Win32_OperatingSystem
    $build = [int]$os.BuildNumber
    $family = if ($build -ge 22000) { 'windows-11' } elseif ($build -ge 10240) { 'windows-10' } else { 'unsupported' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    [ordered]@{
        family = $family
        build = $build
        caption = [string]$os.Caption
        architecture = [string]$os.OSArchitecture
        user = [string]$identity.Name
        userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
}

function Read-DpapiSecret([string]$ProtectedValue) {
    Add-Type -AssemblyName System.Security
    [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String($ProtectedValue), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser))
}

function Wait-Json([string]$Path, [scriptblock]$Predicate, [int]$Seconds = 30) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 150
        $value = if (Test-Path -LiteralPath $Path) {
            try { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $null }
        } else { $null }
        if ($null -ne $value -and (& $Predicate $value)) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for evidence: $Path"
}

$osEvidence = Get-WindowsIdentityEvidence
if ($ExpectedWindows -ne 'auto' -and $osEvidence.family -ne $ExpectedWindows) {
    throw "Expected $ExpectedWindows, detected $($osEvidence.family) build $($osEvidence.build)."
}
if ($ExpectedPrivilege -eq 'admin' -and -not $osEvidence.elevated) { throw 'This matrix row requires an elevated account.' }
if ($ExpectedPrivilege -eq 'non-admin' -and $osEvidence.elevated) { throw 'This matrix row requires a non-administrator account.' }
if ($RequireUnicodeUserProfile -and $osEvidence.userProfile -notmatch '[^\x00-\x7F]') { throw 'This matrix row requires a non-ASCII user profile path.' }

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Open Agent Matrix 中文 路径 ' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot '应用 安装 空格路径'
$workspace = Join-Path $testRoot '工作区 空格路径'
$appData = Join-Path $workspace '.claude-gui-v2'
$copiedExecutable = Join-Path $installRoot ([IO.Path]::GetFileName($executablePath))
$runtimePath = Join-Path $appData 'runtime-state.json'
$uiStatePath = Join-Path $appData 'ui-connection-state.json'
$resultFile = if ($ResultsPath) { [IO.Path]::GetFullPath($ResultsPath) } else { Join-Path $PSScriptRoot 'windows-clean-matrix-result.json' }
$process = $null
$hostPid = 0
$result = [ordered]@{
    schemaVersion = 1
    startedAt = [DateTimeOffset]::Now.ToString('o')
    state = 'running'
    os = $osEvidence
    scenario = [ordered]@{
        edition = $ExpectedEdition
        webViewRuntime = $WebViewRuntime
        expectedPrivilege = $ExpectedPrivilege
        requireClaudeAbsent = [bool]$RequireClaudeAbsent
        requireNonDDrive = [bool]$RequireNonDDrive
        requireUnicodeUserProfile = [bool]$RequireUnicodeUserProfile
        full = [bool]$Full
    }
    checks = [ordered]@{}
}

$savedEnvironment = @{}
foreach ($name in @('CLAUDE_GUI_WORKSPACE','CLAUDE_GUI_ROOT','CLAUDE_GUI_TEST_MODE','CLAUDE_GUI_MUTEX_SCOPE','CLAUDE_GUI_WEBVIEW2_RUNTIME')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    [IO.Directory]::CreateDirectory($installRoot) | Out-Null
    [IO.Directory]::CreateDirectory($workspace) | Out-Null
    Copy-Item -LiteralPath $executablePath -Destination $copiedExecutable -Force
    if ($RequireNonDDrive -and ([IO.Path]::GetPathRoot($copiedExecutable)).TrimEnd('\') -eq 'D:') { throw 'The non-D-drive row is running from D:.' }

    $env:CLAUDE_GUI_WORKSPACE = $workspace
    $env:CLAUDE_GUI_ROOT = $installRoot
    $env:CLAUDE_GUI_TEST_MODE = '1'
    $env:CLAUDE_GUI_MUTEX_SCOPE = 'windows-matrix-' + [guid]::NewGuid().ToString('N')
    if ($WebViewRuntime -eq 'evergreen') { Remove-Item Env:CLAUDE_GUI_WEBVIEW2_RUNTIME -ErrorAction SilentlyContinue }
    else { $env:CLAUDE_GUI_WEBVIEW2_RUNTIME = [IO.Path]::GetFullPath($WebViewRuntime) }

    $process = Start-Process -FilePath $copiedExecutable -ArgumentList '--ui' -WorkingDirectory $workspace -WindowStyle Hidden -PassThru
    $runtime = Wait-Json $runtimePath { param($value) $value.state -eq 'running' }
    $hostPid = [int]$runtime.pid
    $uiState = Wait-Json $uiStatePath { param($value) $value.webView2Version -and @('connected','webview-ready') -contains $value.state } 45
    $secret = Read-DpapiSecret ([string]$runtime.authProtected)
    $headers = @{'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2'}
    $base = "http://127.0.0.1:$($runtime.port)"
    $bootstrap = Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers $headers -TimeoutSec 10
    $worker = Invoke-RestMethod -Uri "$base/api/workbench/runtime/claude?probe=0" -Headers $headers -TimeoutSec 10

    if ($bootstrap.edition.id -ne $ExpectedEdition) { throw "Edition mismatch: $($bootstrap.edition.id)" }
    if ($bootstrap.persistence.integrity -ne 'ok') { throw 'SQLite integrity check failed.' }
    $expectedWebViewMode = if ($WebViewRuntime -eq 'evergreen') { 'evergreen' } else { 'fixed' }
    if ($uiState.webView2Mode -ne $expectedWebViewMode) { throw "WebView2 mode mismatch: $($uiState.webView2Mode)" }
    if ($RequireClaudeAbsent -and [bool]$worker.available) { throw "Claude Code unexpectedly exists at $($worker.path)." }

    $result.checks.freshData = -not (Test-Path -LiteralPath (Join-Path $appData 'migration-backup-*'))
    $result.checks.hostStarted = $true
    $result.checks.uiStarted = -not $process.HasExited
    $result.checks.sqliteIntegrity = $bootstrap.persistence.integrity
    $result.checks.chineseSpacePaths = $workspace -match '[^\x00-\x7F]' -and $workspace.Contains(' ')
    $result.checks.nonDDrive = ([IO.Path]::GetPathRoot($copiedExecutable)).TrimEnd('\') -ne 'D:'
    $result.checks.workerAvailable = [bool]$worker.available
    $result.checks.webView2 = [ordered]@{
        mode = [string]$uiState.webView2Mode
        version = [string]$uiState.webView2Version
        windowsFamily = [string]$uiState.windowsFamily
        windowsBuild = [int]$uiState.windowsBuild
    }

    if ($NodePath) {
        & $NodePath (Join-Path $PSScriptRoot 'native_offline_smoke.js') $runtime.port $secret
        if ($LASTEXITCODE -ne 0) { throw "Offline API smoke failed: $LASTEXITCODE" }
        $result.checks.offlineApi = 'passed'
    } else { $result.checks.offlineApi = 'not-run: NodePath was not supplied' }

    if ($Full) {
        if (-not $DependencyRoot) { throw 'Full matrix mode requires DependencyRoot.' }
        & (Join-Path $PSScriptRoot 'event-store-selftest.ps1') -Executable $copiedExecutable
        & (Join-Path $PSScriptRoot 'native-installer-selftest.ps1') -Executable $copiedExecutable
        & (Join-Path $PSScriptRoot 'webview-runtime-selftest.ps1') -DependencyRoot $DependencyRoot
        & (Join-Path $PSScriptRoot 'agent-worker-sdk-integration.ps1') -Executable $copiedExecutable -DependencyRoot $DependencyRoot
        & (Join-Path $PSScriptRoot 'dsh-worker-sdk-integration.ps1') -Executable $copiedExecutable -NodePath $NodePath
        $result.checks.legacyUpgrade = 'passed by event-store-selftest'
        $result.checks.installUpgradeUninstall = 'passed'
        $result.checks.webViewConfiguration = 'passed'
        $result.checks.agentWorkerSdk = 'passed without Claude dependency'
    }

    $result.state = 'passed'
} catch {
    $result.state = 'failed'
    $result.error = $_.Exception.ToString()
    throw
} finally {
    $result.completedAt = [DateTimeOffset]::Now.ToString('o')
    $result.durationSeconds = [math]::Round(([DateTimeOffset]::Parse($result.completedAt) - [DateTimeOffset]::Parse($result.startedAt)).TotalSeconds, 3)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $resultFile)) | Out-Null
    [IO.File]::WriteAllText($resultFile, ($result | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    if ($hostPid -gt 0) { Stop-Process -Id $hostPid -Force -ErrorAction SilentlyContinue }
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ((Test-Path -LiteralPath $resolvedTestRoot) -and $resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

[pscustomobject]$result | Format-List

param(
    [string]$Root = '',
    [string]$Executable = '',
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
$sourceRoot = if ($Root) { [IO.Path]::GetFullPath($Root) } else { Split-Path -Parent $PSScriptRoot }
$report = if ($ReportPath) { [IO.Path]::GetFullPath($ReportPath) } else { Join-Path $sourceRoot 'security-audit-result.json' }
$failures = [Collections.Generic.List[string]]::new()
$warnings = [Collections.Generic.List[string]]::new()
$files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|\.packages|\.worker-runtimes|node_modules|dist|build|frontend-build|\.venv)[\\/]'
})

$forbiddenFiles = @('.env','.env.local','id_rsa','id_ed25519','settings.json','runtime-state.json','events.db','events.db-wal')
foreach ($file in $files) {
    if ($forbiddenFiles -contains $file.Name -or $file.Extension -in @('.pfx','.p12','.key','.db','.sqlite','.log')) {
        $failures.Add("Forbidden release file: $($file.FullName.Substring($sourceRoot.Length + 1))")
    }
}

$textExtensions = @('.cs','.ps1','.js','.html','.css','.md','.json','.yml','.yaml','.txt')
$personalPatterns = @('C:\\Users\\waseb','D:\\pic\\','AppData\\Local\\Temp\\codex-clipboard')
$credentialPattern = '(?i)(sk-[a-z0-9_-]{24,}|AIza[0-9A-Za-z_-]{25,}|gh[pousr]_[A-Za-z0-9_]{24,})'
foreach ($file in $files | Where-Object { $textExtensions -contains $_.Extension }) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($pattern in $personalPatterns) {
        if ($text -match $pattern) { $failures.Add("Personal or revoked-secret marker in $($file.FullName.Substring($sourceRoot.Length + 1)): $pattern") }
    }
    foreach ($match in [regex]::Matches($text, $credentialPattern)) {
        if ($match.Value -match '(?i)^sk-(tool-runtime|tool-output|provider-|diagnostics-|fixture|test|fake|dummy|must-be)') { continue }
        $lineStart = $text.LastIndexOf("`n", [Math]::Max(0, $match.Index - 1)) + 1
        $lineEnd = $text.IndexOf("`n", $match.Index)
        if ($lineEnd -lt 0) { $lineEnd = $text.Length }
        $line = $text.Substring($lineStart, $lineEnd - $lineStart)
        if ($line -notmatch '(?i)(fixture|redact|example|invalid|dummy|test|must-be|fake)') {
            $failures.Add("Credential-shaped literal without fixture marker in $($file.FullName.Substring($sourceRoot.Length + 1))")
        }
    }
}

foreach ($required in @('LICENSE','SECURITY.md','THIRD_PARTY_NOTICES.md','build\dependencies.lock.json','docs\THREAT_MODEL.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $required))) { $failures.Add("Required release file missing: $required") }
}

$signature = $null
if ($Executable) {
    $executablePath = [IO.Path]::GetFullPath($Executable)
    if (-not (Test-Path -LiteralPath $executablePath)) { $failures.Add("Release executable missing: $executablePath") }
    else {
        $authenticode = Get-AuthenticodeSignature -LiteralPath $executablePath
        $signature = [ordered]@{ status = [string]$authenticode.Status; signer = if ($authenticode.SignerCertificate) { $authenticode.SignerCertificate.Subject } else { '' } }
        if ($authenticode.Status -eq 'NotSigned') { $warnings.Add('Executable is unsigned; release must publish SHA-256 and reproducibility evidence.') }
        elseif ($authenticode.Status -ne 'Valid') { $failures.Add("Executable signature verification failed: $($authenticode.Status)") }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    state = if ($failures.Count -eq 0) { 'passed' } else { 'failed' }
    scannedFiles = $files.Count
    failures = @($failures)
    warnings = @($warnings)
    signature = $signature
    checkedAt = [DateTimeOffset]::Now.ToString('o')
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $report)) | Out-Null
[IO.File]::WriteAllText($report, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
[pscustomobject]$result | Format-List

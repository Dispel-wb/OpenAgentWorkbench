$ErrorActionPreference='Stop'
$scratch=Join-Path ([IO.Path]::GetTempPath()) ('signature-gate-'+[guid]::NewGuid().ToString('N'))
$audit=Join-Path $PSScriptRoot 'release-security-audit.ps1'
[IO.Directory]::CreateDirectory($scratch)|Out-Null
try {
    foreach($relative in @('LICENSE','SECURITY.md','THIRD_PARTY_NOTICES.md','build/dependencies.lock.json','docs/THREAT_MODEL.md','candidate.exe')) {
        $file=Join-Path $scratch $relative
        [IO.Directory]::CreateDirectory((Split-Path $file -Parent))|Out-Null
        [IO.File]::WriteAllText($file,'fixture')
    }
    # Inject only OS signature results to test policy; this does not certify a real signed artifact.
    function Get-AuthenticodeSignature { param($LiteralPath) [pscustomobject]@{Status=$signatureFixtureStatus;SignerCertificate=$null} }
    $results=@()
    foreach($signatureFixtureStatus in @('Valid','NotSigned','HashMismatch','NotTrusted','UnknownError','NotSupportedFileFormat','Incompatible','UnexpectedStatus')) {
        $report=Join-Path $scratch 'report.json'
        $rejected=$false
        try { & $audit -Root $scratch -Executable (Join-Path $scratch 'candidate.exe') -ReportPath $report | Out-Null } catch { $rejected=$true }
        $expected=$signatureFixtureStatus -notin @('Valid','NotSigned')
        $data=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
        if($rejected -ne $expected -or ($data.state -eq 'failed') -ne $expected){throw "Signature policy mismatch: $signatureFixtureStatus ($($data.failures -join ', '))"}
        if($signatureFixtureStatus -eq 'NotSigned' -and $data.warnings.Count -ne 1){throw 'Unsigned disclosure missing'}
        $results+=$signatureFixtureStatus
    }
    [pscustomobject]@{SignaturePolicy='PASS';Cases=$results.Count;Statuses=$results}|ConvertTo-Json -Compress
} finally {
    $resolved=[IO.Path]::GetFullPath($scratch)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved).StartsWith('signature-gate-')){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

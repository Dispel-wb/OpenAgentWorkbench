param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$OutputPath = '',
    [string]$Version = '1.0.0.0',
    [string]$IdentityName = 'OpenAgentWorkbench.UnsignedTest',
    [string]$Publisher = 'CN=OpenAgentWorkbench Development, OID.2.25.311729368913984317654407730594956997722=1',
    [string]$PublisherDisplayName = 'Open Agent Workbench',
    [string]$PythonPath = '',
    [switch]$StoreSubmission
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceExe = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw "Executable does not exist: $sourceExe" }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'MSIX version must contain four numeric parts, for example 1.0.0.0.' }
if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw 'IdentityName must be 3-50 ASCII letters, digits, periods, or hyphens.' }
if ([string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PublisherDisplayName)) { throw 'Publisher values cannot be empty.' }
if ($StoreSubmission -and ($IdentityName -eq 'OpenAgentWorkbench.UnsignedTest' -or $Publisher -match 'OpenAgentWorkbench Development')) {
    throw 'StoreSubmission requires the exact Package/Identity/Name and Publisher values from Partner Center.'
}

function Find-Python {
    param([string]$Requested)
    $candidates = @($Requested, (Join-Path $root '.venv\Scripts\python.exe'), (Get-Command python -ErrorAction SilentlyContinue).Source) | Where-Object { $_ }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        & $candidate -c 'from PIL import Image; import sys; sys.exit(0)' *> $null
        if ($LASTEXITCODE -eq 0) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'Python with Pillow is required to generate MSIX visual assets. Pass -PythonPath.'
}

function Find-MakeAppx {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
        Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $candidate) { throw 'Windows SDK MakeAppx.exe was not found. Install the Windows 10/11 SDK.' }
    return $candidate
}

function Xml-Escape([string]$Value) { return [Security.SecurityElement]::Escape($Value) }

$python = Find-Python $PythonPath
$makeAppx = Find-MakeAppx
$dist = Join-Path $root 'dist\msix'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $dist "OpenAgentWorkbench-$Version-x64.msix" }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null

$stage = Join-Path $dist ('layout-' + [Guid]::NewGuid().ToString('N'))
$stageRoot = [IO.Path]::GetFullPath($stage)
if (-not $stageRoot.StartsWith(([IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe MSIX staging path.' }
New-Item -ItemType Directory -Path (Join-Path $stageRoot 'Assets') -Force | Out-Null

try {
    $packageExe = Join-Path $stageRoot 'OpenAgentWorkbench.exe'
    Copy-Item -LiteralPath $sourceExe -Destination $packageExe -Force
    $sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $packageExe -Algorithm SHA256).Hash -ne $sourceHash) { throw 'EXE hash changed while staging the MSIX package.' }

    $iconSource = Join-Path $root 'build\open-agent.png'
    if (-not (Test-Path -LiteralPath $iconSource -PathType Leaf)) { $iconSource = Join-Path $root 'static\assets\mascot.png' }
    $assetScript = @'
from PIL import Image
import os, sys
source, target = sys.argv[1], sys.argv[2]
image = Image.open(source).convert("RGBA")
box = image.getbbox()
if box:
    image = image.crop(box)
for name, size in [("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50)]:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    glyph = image.copy()
    margin = max(2, int(size * 0.08))
    glyph.thumbnail((size - 2 * margin, size - 2 * margin), Image.Resampling.LANCZOS)
    canvas.alpha_composite(glyph, ((size - glyph.width) // 2, (size - glyph.height) // 2))
    canvas.save(os.path.join(target, name), optimize=True)
wide = Image.new("RGBA", (310, 150), (0, 0, 0, 0))
glyph = image.copy(); glyph.thumbnail((116, 116), Image.Resampling.LANCZOS)
wide.alpha_composite(glyph, ((310 - glyph.width) // 2, (150 - glyph.height) // 2))
wide.save(os.path.join(target, "Wide310x150Logo.png"), optimize=True)
'@
    & $python -c $assetScript $iconSource (Join-Path $stageRoot 'Assets')
    if ($LASTEXITCODE -ne 0) { throw "MSIX asset generation failed: $LASTEXITCODE" }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap desktop rescap">
  <Identity Name="$(Xml-Escape $IdentityName)" Publisher="$(Xml-Escape $Publisher)" Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Open Agent Workbench</DisplayName>
    <PublisherDisplayName>$(Xml-Escape $PublisherDisplayName)</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="OpenAgentWorkbench.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="Open Agent Workbench" Description="Local desktop workbench for Agent tasks" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
      </uap:VisualElements>
      <Extensions>
        <desktop:Extension Category="windows.startupTask" Executable="OpenAgentWorkbench.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="OpenAgentWorkbenchHost" Enabled="false" DisplayName="Open Agent Workbench Host" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
    [IO.File]::WriteAllText((Join-Path $stageRoot 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
    & $makeAppx pack /d $stageRoot /p $output /o
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "MakeAppx failed: $LASTEXITCODE" }
    $report = [ordered]@{
        package = $output
        packageSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        sourceExecutable = $sourceExe
        sourceExecutableSha256 = $sourceHash
        identityName = $IdentityName
        publisher = $Publisher
        version = $Version
        storeSubmission = [bool]$StoreSubmission
        signed = $false
    }
    $reportPath = [IO.Path]::ChangeExtension($output, '.json')
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $report | ConvertTo-Json -Depth 4
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        $resolved = [IO.Path]::GetFullPath($stageRoot)
        if (-not $resolved.StartsWith(([IO.Path]::GetFullPath($dist).TrimEnd('\') + '\layout-'), [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing unsafe cleanup: $resolved" }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

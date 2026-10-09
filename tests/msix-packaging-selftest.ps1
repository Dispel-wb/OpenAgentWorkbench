param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$PythonPath = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'dist\msix-selftest' }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$package = Join-Path $outputRoot 'OpenAgentWorkbench-unsigned-test.msix'
& (Join-Path $root 'build_msix.ps1') -Executable $Executable -OutputPath $package -PythonPath $PythonPath
if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed.' }

$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $kits -Directory | Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending | ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $makeAppx) { throw 'MakeAppx.exe not found.' }
$unpack = Join-Path $outputRoot ('unpacked-' + [Guid]::NewGuid().ToString('N'))
try {
    & $makeAppx unpack /p $package /d $unpack /o
    if ($LASTEXITCODE -ne 0) { throw 'MSIX unpack failed.' }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $unpack 'AppxManifest.xml') -Raw
    $manager = New-Object Xml.XmlNamespaceManager($manifest.NameTable)
    $manager.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $manager.AddNamespace('desktop', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $manager.AddNamespace('rescap', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
    if (-not $manifest.SelectSingleNode('//rescap:Capability[@Name="runFullTrust"]', $manager)) { throw 'MSIX is missing runFullTrust.' }
    if (-not $manifest.SelectSingleNode('//desktop:Extension[@Category="windows.startupTask"]', $manager)) { throw 'MSIX is missing the startupTask extension.' }
    $packedExe = Join-Path $unpack 'OpenAgentWorkbench.exe'
    if ((Get-FileHash -LiteralPath $packedExe -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash) { throw 'Packaged EXE hash does not match the candidate.' }
    foreach ($asset in @('Square44x44Logo.png','Square150x150Logo.png','StoreLogo.png','Wide310x150Logo.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $unpack "Assets\$asset") -PathType Leaf)) { throw "Missing MSIX asset: $asset" }
    }
    Write-Output "msix-packaging-selftest: ok`n$package"
}
finally {
    if (Test-Path -LiteralPath $unpack) { Remove-Item -LiteralPath $unpack -Recurse -Force }
}

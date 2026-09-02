param([string]$Destination = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$lockPath = Join-Path $root 'build\dependencies.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$destinationPath = if ($Destination) { [IO.Path]::GetFullPath($Destination) } else { Join-Path $root '.packages' }
$resolvedRoot = [IO.Path]::GetFullPath($root)
if (-not $destinationPath.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Dependency destination must remain inside the source tree.'
}
[IO.Directory]::CreateDirectory($destinationPath) | Out-Null

foreach ($package in $lock.packages) {
    $id = ([string]$package.id).ToLowerInvariant()
    $version = [string]$package.version
    $packageRoot = Join-Path $destinationPath (Join-Path $id $version)
    $marker = Join-Path $packageRoot '.restored.json'
    if (Test-Path -LiteralPath $marker) {
        $existing = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($existing.nupkgSha256 -eq $package.sha256) { continue }
    }

    $download = Join-Path $destinationPath ($id + '.' + $version + '.nupkg')
    $zip = Join-Path $destinationPath ($id + '.' + $version + '.zip')
    Invoke-WebRequest -UseBasicParsing -Uri ("https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg" -f $id, $version) -OutFile $download
    $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    if ($actual -ne $package.sha256) { throw "NuGet package hash mismatch for $id $version. Expected $($package.sha256), got $actual." }

    $resolvedPackageRoot = [IO.Path]::GetFullPath($packageRoot)
    if (Test-Path -LiteralPath $resolvedPackageRoot) {
        if (-not $resolvedPackageRoot.StartsWith($destinationPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package cleanup path.' }
        Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
    }
    Copy-Item -LiteralPath $download -Destination $zip -Force
    Expand-Archive -LiteralPath $zip -DestinationPath $resolvedPackageRoot -Force
    [IO.File]::WriteAllText($marker, (@{ id = $id; version = $version; nupkgSha256 = $actual } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $download,$zip -Force
}

$required = @(
    'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe',
    'microsoft.web.webview2\1.0.3856.49\lib\net462\Microsoft.Web.WebView2.Core.dll',
    'microsoft.web.webview2\1.0.3856.49\lib\net462\Microsoft.Web.WebView2.WinForms.dll',
    'microsoft.web.webview2\1.0.3856.49\runtimes\win-x64\native\WebView2Loader.dll',
    'newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $destinationPath $relative) -PathType Leaf)) { throw "Restored dependency is missing: $relative" }
}
Write-Output $destinationPath

param([string]$InstallRoot='')
$ErrorActionPreference='Stop'
if(-not $InstallRoot){$InstallRoot=$PSScriptRoot}
$target=Join-Path ([IO.Path]::GetFullPath($InstallRoot)) 'runtimes\dsh'
$source=Join-Path $PSScriptRoot 'runtimes\dsh'
$node=(Get-Command node -ErrorAction Stop).Source
$version=& $node --version
if([version]$version.TrimStart('v') -lt [version]'22.19.0'){throw 'DSHarness requires Node.js 22.19 or newer (24.x recommended).'}
[IO.Directory]::CreateDirectory($target)|Out-Null
if([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)){
    Copy-Item -LiteralPath (Join-Path $source 'package.json'),(Join-Path $source 'package-lock.json') -Destination $target -Force
}
# A dedicated dependency directory: never run npm ci at the app or workspace root.
& npm ci --prefix $target --legacy-peer-deps --ignore-scripts --no-audit --no-fund
if($LASTEXITCODE -ne 0){throw 'Locked DSHarness runtime installation failed.'}
$entry=Join-Path $target 'node_modules\@deepseek-ai\dsh\lib\bin.js'
$coreVersion=& $node $entry --version
if($LASTEXITCODE -ne 0 -or $coreVersion -ne '0.1.2-alpha.5'){throw "Unexpected or broken DSHarness runtime: $coreVersion"}
[pscustomobject]@{Core='DSHarness CLI';Version=$coreVersion;Entry=$entry;Node=$node;PaidApiCalls=0}|Format-List

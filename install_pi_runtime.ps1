param([string]$InstallRoot='')
$ErrorActionPreference='Stop'
if(-not $InstallRoot){$InstallRoot=$PSScriptRoot}
$target=Join-Path ([IO.Path]::GetFullPath($InstallRoot)) 'runtimes\pi'
$source=Join-Path $PSScriptRoot 'runtimes\pi'
$node=(Get-Command node -ErrorAction Stop).Source
$version=& $node --version
if([version]$version.TrimStart('v') -lt [version]'22.19.0'){throw 'Pi requires Node.js 22.19 or newer.'}
[IO.Directory]::CreateDirectory($target)|Out-Null
if([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)){
    Copy-Item -LiteralPath (Join-Path $source 'package.json'),(Join-Path $source 'package-lock.json') -Destination $target -Force
}
# npm ci replaces only this dedicated runtime's node_modules, never the workspace.
& npm.cmd ci --prefix $target --ignore-scripts --no-audit --no-fund
if($LASTEXITCODE -ne 0){throw 'Locked Pi runtime installation failed.'}
$entry=Join-Path $target 'node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'
$coreVersion=& $node $entry --version
if($LASTEXITCODE -ne 0 -or $coreVersion -ne '0.85.1'){throw "Unexpected or broken Pi runtime: $coreVersion"}
[pscustomobject]@{Core='Pi Coding Agent';Version=$coreVersion;Entry=$entry;Node=$node;PaidApiCalls=0}|Format-List

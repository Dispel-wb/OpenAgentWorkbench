param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$PiRuntimeRoot=(Join-Path $PSScriptRoot '..\runtimes\pi'),
    [string]$NodePath=(Get-Command node -ErrorAction Stop).Source
)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$PiRuntimeRoot=(Resolve-Path -LiteralPath $PiRuntimeRoot).Path
$NodePath=(Resolve-Path -LiteralPath $NodePath).Path
$destinationRoot=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $destinationRoot){throw 'Freeze destination must be new; earlier evidence must be preserved'}
$entry=Join-Path $PiRuntimeRoot 'node_modules\@earendil-works\pi-coding-agent\dist\bundle\cli.js'
if(-not(Test-Path -LiteralPath $entry -PathType Leaf)){throw 'Restore the locked Pi runtime before freezing'}
if(@(Get-ChildItem -LiteralPath $PiRuntimeRoot -Recurse -Force|Where-Object{ $_.Attributes-band[IO.FileAttributes]::ReparsePoint }).Count){throw 'Freeze inputs must not contain filesystem links'}
[IO.Directory]::CreateDirectory($destinationRoot)|Out-Null
[IO.Directory]::CreateDirectory((Join-Path $destinationRoot 'tests'))|Out-Null
[IO.Directory]::CreateDirectory((Join-Path $destinationRoot 'tools'))|Out-Null
[IO.Directory]::CreateDirectory((Join-Path $destinationRoot 'runtimes'))|Out-Null
Copy-Item -LiteralPath $Executable -Destination (Join-Path $destinationRoot 'OpenAgentWorkbench.exe')
Copy-Item -LiteralPath $NodePath -Destination (Join-Path $destinationRoot 'tools\node.exe')
Copy-Item -LiteralPath $PiRuntimeRoot -Destination (Join-Path $destinationRoot 'runtimes\pi') -Recurse
foreach($name in @('pi-host-integration.ps1','pi-host-fixture.js','start-pi-soak.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $destinationRoot "tests\$name")}
$files=@(Get-ChildItem -LiteralPath $destinationRoot -Recurse -File|Sort-Object FullName|ForEach-Object{@{path=$_.FullName.Substring($destinationRoot.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$manifest=[ordered]@{schemaVersion=1;createdAt=[DateTimeOffset]::UtcNow.ToString('o');candidateSha256=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash;files=$files}
[IO.File]::WriteAllText((Join-Path $destinationRoot 'input-manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Freeze='CREATED';CandidateSha256=$manifest.candidateSha256;Files=$files.Count;Directory=$destinationRoot}|ConvertTo-Json -Compress

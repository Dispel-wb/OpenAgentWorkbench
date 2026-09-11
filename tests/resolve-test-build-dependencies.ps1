param([string]$DependencyRoot=$env:WORKBENCH_TEST_DEPENDENCY_ROOT)
$ErrorActionPreference='Stop'
if(-not$DependencyRoot){$DependencyRoot=Join-Path $PSScriptRoot '..\.packages'}
$DependencyRoot=(Resolve-Path -LiteralPath $DependencyRoot).Path
$lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\build\dependencies.lock.json') -Raw|ConvertFrom-Json
$compilerVersion=($lock.packages|Where-Object id -eq 'microsoft.net.compilers.toolset').version
$jsonVersion=($lock.packages|Where-Object id -eq 'newtonsoft.json').version
if(-not$compilerVersion-or-not$jsonVersion){throw 'Compiler and JSON dependencies must be declared in the build lock'}
$compiler=Join-Path $DependencyRoot "microsoft.net.compilers.toolset\$compilerVersion\tasks\net472\csc.exe"
$json=Join-Path $DependencyRoot "newtonsoft.json\$jsonVersion\lib\net45\Newtonsoft.Json.dll"
foreach($file in @($compiler,$json)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw 'Restore the locked build dependencies before running offline tests'}}
[pscustomobject]@{Compiler=$compiler;Json=$json}

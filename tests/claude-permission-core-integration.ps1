param([Parameter(Mandatory=$true)][string]$Executable,[string]$Node='C:\Program Files\nodejs\node.exe')
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('permission-core-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$fixture=$null
try {
    $portFile=Join-Path $root 'port.txt'
    $fixture=Start-Process $Node -ArgumentList @((Join-Path $PSScriptRoot 'claude-permission-core-fixture.js'),$portFile) -WindowStyle Hidden -PassThru
    $expires=(Get-Date).AddSeconds(10)
    while(-not(Test-Path $portFile)-and(Get-Date)-lt$expires){Start-Sleep -Milliseconds 100}
    $port=[int](Get-Content $portFile -Raw)
    Add-Type -AssemblyName System.Security
    $protected=[Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('offline-permission-fixture'),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    $provider=@{id='permission-fixture';name='Offline permission fixture';tokenEncrypted=$protected;authStyle='bearer';text=@{enabled=$true;protocol='openai';baseUrl="http://127.0.0.1:$port/v1";models=@('fixture-tool-model')};image=@{enabled=$false;protocol='openai-images';models=@()}}
    [IO.File]::WriteAllText((Join-Path $root 'providers-v2.json'),(ConvertTo-Json -InputObject @($provider) -Depth 8),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'settings.json'),'{"providerId":"permission-fixture","model":"fixture-tool-model"}',[Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'live-workbench-acceptance.ps1') -Executable $Executable -SourceData $root -AllowPaid -Scenario all -VerifyBoundary -OutputDirectory (Join-Path $PSScriptRoot '..\dist\permission-core')
    Write-Output 'PASS genuine Claude core: MCP handshake, scoped Read/Edit, outside write denied, Chinese paths, second-turn recall, cross-parent DAG. No paid requests.'
}finally{
    if(Test-Path ($portFile+'.tools.json')){Copy-Item -LiteralPath ($portFile+'.tools.json') -Destination (Join-Path $PSScriptRoot '..\dist\permission-core\tools.json')}
    if(Test-Path ($portFile+'.failure.json')){Copy-Item -LiteralPath ($portFile+'.failure.json') -Destination (Join-Path $PSScriptRoot '..\dist\permission-core\failure.json')}
    if($fixture-and-not$fixture.HasExited){Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue}
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved).StartsWith('permission-core-')){Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue}
}

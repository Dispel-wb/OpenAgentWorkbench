param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference='Stop';$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-installer-'+[guid]::NewGuid().ToString('N'))
$env:CLAUDE_GUI_INSTALL_TEST='1'
try{
 $first=Start-Process $Executable -ArgumentList @('--install',$root) -WindowStyle Hidden -Wait -PassThru
 if($first.ExitCode-ne0){throw "First install failed: $($first.ExitCode)"}
 $manifest=Join-Path $root 'install-manifest.json'
 if(-not(Test-Path $manifest)){throw 'Install manifest missing'}
 $installState=Get-Content -LiteralPath $manifest -Raw -Encoding UTF8|ConvertFrom-Json
 $installed=[IO.Path]::GetFullPath([string]$installState.executable)
 $exeName=[IO.Path]::GetFileName($installed)
 if(-not(Test-Path $installed)){throw 'Installed executable missing'}
 $second=Start-Process $Executable -ArgumentList @('--install',$root) -WindowStyle Hidden -Wait -PassThru
 if($second.ExitCode-ne0-or-not(Test-Path (Join-Path $root ([IO.Path]::GetFileNameWithoutExtension($exeName)+'.previous.exe')))){throw 'Atomic upgrade did not preserve previous'}
 $unsafe=Join-Path ([IO.Path]::GetTempPath()) ('claude-installer-guard-'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($unsafe)|Out-Null
 try {
   $unsafeManifest=$installState.PSObject.Copy();$unsafeManifest.executable=$installed
   [IO.File]::WriteAllText((Join-Path $unsafe 'install-manifest.json'),($unsafeManifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
   $guard=Start-Process $Executable -ArgumentList @('--uninstall',$unsafe) -WindowStyle Hidden -Wait -PassThru
   if($guard.ExitCode-ne82-or-not(Test-Path $unsafe)){throw 'Uninstall accepted a copied manifest outside its installation directory'}
 } finally {Remove-Item -LiteralPath $unsafe -Recurse -Force -ErrorAction SilentlyContinue}
 $uninstall=Start-Process $installed -ArgumentList @('--uninstall',$root) -WindowStyle Hidden -Wait -PassThru
 if($uninstall.ExitCode-ne0){throw "Uninstall launcher failed: $($uninstall.ExitCode)"}
 $expires=(Get-Date).AddSeconds(20);while((Test-Path $root)-and(Get-Date)-lt$expires){Start-Sleep -Milliseconds 100}
 if(Test-Path $root){throw 'Native uninstall did not remove test installation'}
 [pscustomobject]@{NativeInstall='OK';AtomicUpgrade='OK';PreviousVersion='OK';UninstallBoundary='OK';NativeUninstall='OK';BatchDependency='None'}|Format-List
}finally{Remove-Item Env:CLAUDE_GUI_INSTALL_TEST -ErrorAction SilentlyContinue}

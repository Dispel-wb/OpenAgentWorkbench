param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference='Stop';$root=Join-Path ([IO.Path]::GetTempPath()) ('claude-installer-'+[guid]::NewGuid().ToString('N'))
$env:CLAUDE_GUI_INSTALL_TEST='1'
try{
 $first=Start-Process $Executable -ArgumentList @('--install',$root) -WindowStyle Hidden -Wait -PassThru
 if($first.ExitCode-ne0){throw "First install failed: $($first.ExitCode)"}
 $exeName=[IO.Path]::GetFileName($Executable)
 $installed=Join-Path $root $exeName;$manifest=Join-Path $root 'install-manifest.json'
 if(-not(Test-Path $installed)-or-not(Test-Path $manifest)){throw 'Installed files missing'}
 $second=Start-Process $Executable -ArgumentList @('--install',$root) -WindowStyle Hidden -Wait -PassThru
 if($second.ExitCode-ne0-or-not(Test-Path (Join-Path $root ([IO.Path]::GetFileNameWithoutExtension($exeName)+'.previous.exe')))){throw 'Atomic upgrade did not preserve previous'}
 $uninstall=Start-Process $installed -ArgumentList @('--uninstall',$root) -WindowStyle Hidden -Wait -PassThru
 if($uninstall.ExitCode-ne0){throw "Uninstall launcher failed: $($uninstall.ExitCode)"}
 $expires=(Get-Date).AddSeconds(20);while((Test-Path $root)-and(Get-Date)-lt$expires){Start-Sleep -Milliseconds 100}
 if(Test-Path $root){throw 'Native uninstall did not remove test installation'}
 [pscustomobject]@{NativeInstall='OK';AtomicUpgrade='OK';PreviousVersion='OK';NativeUninstall='OK';BatchDependency='None'}|Format-List
}finally{Remove-Item Env:CLAUDE_GUI_INSTALL_TEST -ErrorAction SilentlyContinue}

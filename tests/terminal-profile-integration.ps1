param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20) {
    $expires = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 90; try { $value = & $Action } catch { $value = $null }; if (& $Predicate $value) { return $value } } while ((Get-Date) -lt $expires)
    throw ('Timed out waiting for terminal profile integration state: '+$script:stage)
}

function Connect-Host([string]$RuntimePath) {
    $runtime = Wait-Until { if (Test-Path -LiteralPath $RuntimePath) { Get-Content -LiteralPath $RuntimePath -Raw -Encoding UTF8 | ConvertFrom-Json } } { param($value) $null -ne $value -and $value.state -eq 'running' }
    Add-Type -AssemblyName System.Security
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String([string]$runtime.authProtected), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return @{ Runtime=$runtime; Base="http://127.0.0.1:$($runtime.port)"; Headers=@{'X-Desktop-Secret'=[Text.Encoding]::UTF8.GetString($plain);'X-Workbench-Protocol'='2'} }
}

function Api($Connection, [string]$Path, [string]$Method = 'GET', $Body = $null) {
    $request = @{ Uri=$Connection.Base+$Path; Headers=$Connection.Headers; Method=$Method }
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12 -Compress)) }
    Invoke-RestMethod @request
}

function Wait-TerminalText($Connection, [string]$Id, [string]$Expected) {
    $buffer = ''
    Wait-Until {
        $value = Api $Connection ("/api/workbench/terminal/poll?id="+[uri]::EscapeDataString($Id))
        $script:terminalBuffer += [string]$value.output
        $script:terminalBuffer
    } { param($value) ([string]$value).Contains($Expected) } | Out-Null
    return $script:terminalBuffer
}

$root = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('claude-terminal-profile-' + [guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe terminal fixture root' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$env:CLAUDE_GUI_WORKSPACE = $root
$env:CLAUDE_GUI_ROOT = 'D:\softwares\ClaudeCode'
$env:CLAUDE_GUI_TEST_MODE = '1'
$env:CLAUDE_GUI_MUTEX_SCOPE = 'terminal-profile-' + [guid]::NewGuid().ToString('N')
$runtimePath = Join-Path $root '.claude-gui-v2\runtime-state.json'
$hostProcess = $null
$childMarker = 'WORKBENCH_TERMINAL_CHILD_' + [guid]::NewGuid().ToString('N')
try {
    $script:stage='Host startup'
    $hostProcess = Start-Process -FilePath $Executable -ArgumentList '--host' -WorkingDirectory $root -WindowStyle Hidden -PassThru
    $connection = Connect-Host $runtimePath
    $profileResponse = Api $connection '/api/workbench/terminal/profiles'
    $profiles = if ($profileResponse.PSObject.Properties['value']) { @($profileResponse.value) } else { @($profileResponse) }
    if (-not @($profiles|Where-Object id -eq 'powershell').Count -or -not @($profiles|Where-Object id -eq 'cmd').Count) { throw ('Required terminal profiles were not reported: '+($profiles|ConvertTo-Json -Depth 6 -Compress)) }

    $powerShellTerminal = Api $connection '/api/workbench/terminal/start' 'POST' @{workspace=$root;shell='powershell'}
    if ($powerShellTerminal.shell -ne 'powershell') { throw 'PowerShell profile was not selected' }
    Api $connection '/api/workbench/terminal/resize' 'POST' @{id=$powerShellTerminal.id;columns=101;rows=31} | Out-Null
    $script:terminalBuffer = ''
    $script:stage='PowerShell output'
    Api $connection '/api/workbench/terminal/write' 'POST' @{id=$powerShellTerminal.id;command="Write-Output ('PROFILE_' + 'POWERSHELL')";submit=$true} | Out-Null
    $powerShellOutput = Wait-TerminalText $connection $powerShellTerminal.id 'PROFILE_POWERSHELL'
    if ($powerShellOutput.Contains("`n>> ")) { throw 'PowerShell command entered continuation mode after Enter' }
    if ($powerShellOutput.Contains('不可信发布者') -or $powerShellOutput.Contains('untrusted publisher')) { throw 'PowerShell was blocked by an inherited module publisher prompt' }
    Api $connection '/api/workbench/terminal/stop' 'POST' @{id=$powerShellTerminal.id} | Out-Null

    $cmdTerminal = Api $connection '/api/workbench/terminal/start' 'POST' @{workspace=$root;shell='cmd'}
    if ($cmdTerminal.shell -ne 'cmd') { throw 'Command Prompt profile was not selected' }
    $script:terminalBuffer = ''
    $script:stage='Command Prompt output'
    Api $connection '/api/workbench/terminal/write' 'POST' @{id=$cmdTerminal.id;command='set WORKBENCH_PROFILE_SUFFIX=CMD';submit=$true} | Out-Null
    Api $connection '/api/workbench/terminal/write' 'POST' @{id=$cmdTerminal.id;command='echo PROFILE_%WORKBENCH_PROFILE_SUFFIX%';submit=$true} | Out-Null
    $cmdOutput = Wait-TerminalText $connection $cmdTerminal.id 'PROFILE_CMD'

    # Keep the child attached to the ConPTY shell. `start /b` may move it into a
    # separate process group and does not exercise the terminal job tree reliably.
    $childCommand = 'cmd.exe /D /Q /C "title ' + $childMarker + ' & ping -t 127.0.0.1 >nul"'
    Api $connection '/api/workbench/terminal/write' 'POST' @{id=$cmdTerminal.id;command=$childCommand;submit=$true} | Out-Null
    $script:stage='Child process start'
    $child = Wait-Until { @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like ('*'+$childMarker+'*') }) } { param($value) @($value).Count -gt 0 }
    $childPids = @($child | ForEach-Object { [int]$_.ProcessId })
    Api $connection '/api/workbench/terminal/stop' 'POST' @{id=$cmdTerminal.id} | Out-Null
    $script:stage='Child process termination'
    $childExitDeadline = (Get-Date).AddSeconds(20)
    $childTreeTerminated = $false
    do {
        Start-Sleep -Milliseconds 90
        $remainingChild = @(Get-CimInstance Win32_Process | Where-Object { $childPids -contains [int]$_.ProcessId })
        if ($remainingChild.Count -eq 0) { $childTreeTerminated = $true; break }
    } while ((Get-Date) -lt $childExitDeadline)
    if (-not $childTreeTerminated) {
        throw ('Terminal child process tree survived stop: ' + (@($remainingChild | Select-Object ProcessId,ParentProcessId,Name,CommandLine) | ConvertTo-Json -Compress))
    }

    [pscustomobject]@{
        TerminalProfiles='PASS';PowerShell='OK';CommandPrompt='OK';Resize='101x31';EnterUsesCR='OK'
        ChildProcessTreeTerminated='OK';ProfileCount=$profiles.Count;Workspace=$root
    } | Format-List
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like ('*'+$childMarker+'*') } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:CLAUDE_GUI_WORKSPACE,Env:CLAUDE_GUI_ROOT,Env:CLAUDE_GUI_TEST_MODE,Env:CLAUDE_GUI_MUTEX_SCOPE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $root) -and $root.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $root -Recurse -Force }
}

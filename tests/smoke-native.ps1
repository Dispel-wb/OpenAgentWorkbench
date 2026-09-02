param(
    [string]$RuntimeFile = 'D:\work\Claude\.claude-gui-v2\runtime-state.json',
    [string]$Workspace = 'D:\work\Claude'
)

$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "SMOKE FAILED: $Message" }
}

Assert-True (Test-Path -LiteralPath $RuntimeFile) 'runtime-state.json missing'
$runtime = Get-Content -Raw -LiteralPath $RuntimeFile -Encoding UTF8 | ConvertFrom-Json
Assert-True ($runtime.state -eq 'running') 'backend is not running'
Assert-True ([int]($runtime.port) -gt 0) 'local port is invalid'

$baseUrl = "http://127.0.0.1:$($runtime.port)"
$page = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/"
Assert-True ($page.StatusCode -eq 200) 'index page is unavailable'
Assert-True ($page.Content -notmatch 'desktop-secret|__DESKTOP_SECRET__') 'desktop secret leaked into HTML'
Assert-True ([int]$runtime.protocolVersion -eq 2) 'IPC protocol mismatch'
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$runtime.authProtected)) 'protected Host credential missing'
Add-Type -AssemblyName System.Security
$encrypted = [Convert]::FromBase64String([string]$runtime.authProtected)
$plain = [Security.Cryptography.ProtectedData]::Unprotect($encrypted, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
$secret = [Text.Encoding]::UTF8.GetString($plain)
$headers = @{ 'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2' }

$bootstrap = Invoke-RestMethod -Uri "$baseUrl/api/bootstrap" -Headers $headers
$projects = @(Invoke-RestMethod -Uri "$baseUrl/api/workbench/projects" -Headers $headers)
$transcripts = @(Invoke-RestMethod -Uri "$baseUrl/api/workbench/transcripts?workspace=$([uri]::EscapeDataString($Workspace))" -Headers $headers)
$tree = Invoke-RestMethod -Uri "$baseUrl/api/workbench/tree?workspace=$([uri]::EscapeDataString($Workspace))&path=" -Headers $headers
$git = Invoke-RestMethod -Uri "$baseUrl/api/workbench/git/status?workspace=$([uri]::EscapeDataString($Workspace))" -Headers $headers
$extensions = Invoke-RestMethod -Uri "$baseUrl/api/workbench/extensions?workspace=$([uri]::EscapeDataString($Workspace))" -Headers $headers
$usage = Invoke-RestMethod -Uri "$baseUrl/api/workbench/usage?workspace=$([uri]::EscapeDataString($Workspace))" -Headers $headers
$health = Invoke-RestMethod -Uri "$baseUrl/api/workbench/health?workspace=$([uri]::EscapeDataString($Workspace))" -Headers $headers
$reliability = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/reliability/selftest" -Headers $headers -ContentType 'application/json' -Body '{}'
$schedules = @(Invoke-RestMethod -Uri "$baseUrl/api/workbench/schedules" -Headers $headers)
$terminal = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/terminal" -Headers $headers -ContentType 'application/json' -Body (@{ workspace = $Workspace; command = 'echo CLAUDE_WORKBENCH_SMOKE_OK' } | ConvertTo-Json)
$conpty = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/terminal/start" -Headers $headers -ContentType 'application/json' -Body (@{ workspace = $Workspace } | ConvertTo-Json)
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/terminal/resize" -Headers $headers -ContentType 'application/json' -Body (@{ id = $conpty.id; columns = 101; rows = 31 } | ConvertTo-Json) | Out-Null
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/terminal/write" -Headers $headers -ContentType 'application/json' -Body (@{ id = $conpty.id; command = 'echo CLAUDE_CONPTY_OK'; submit = $true } | ConvertTo-Json) | Out-Null
$conptyOutput = ''
for ($attempt = 0; $attempt -lt 30 -and $conptyOutput -notmatch 'CLAUDE_CONPTY_OK'; $attempt++) {
    Start-Sleep -Milliseconds 80
    $chunk = Invoke-RestMethod -Uri "$baseUrl/api/workbench/terminal/poll?id=$($conpty.id)" -Headers $headers
    $conptyOutput += [string]$chunk.output
}
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/terminal/stop" -Headers $headers -ContentType 'application/json' -Body (@{ id = $conpty.id } | ConvertTo-Json) | Out-Null
$skinFixture = Join-Path ([IO.Path]::GetTempPath()) ("claude-skin-smoke-" + [guid]::NewGuid().ToString('N'))
$skinSource = Join-Path $skinFixture 'source'
$skinZip = Join-Path $skinFixture 'skin.zip'
[IO.Directory]::CreateDirectory($skinSource) | Out-Null
[IO.File]::WriteAllText((Join-Path $skinSource 'manifest.json'), '{"id":"zip-smoke","name":"ZIP Smoke","css":"skin.css","assets":{},"development":true,"minAppVersion":"6.1.0","maxAppVersion":"6.9.0"}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $skinSource 'skin.css'), 'html[data-skin="zip-smoke"]{--claude:#6688aa}', [Text.UTF8Encoding]::new($false))
Compress-Archive -Path (Join-Path $skinSource '*') -DestinationPath $skinZip
$skinImport = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/skins/import" -Headers $headers -ContentType 'application/json' -Body (@{ path = $skinZip } | ConvertTo-Json)
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/skins/delete" -Headers $headers -ContentType 'application/json' -Body (@{ id = 'zip-smoke' } | ConvertTo-Json) | Out-Null
$unsignedRejected = $false
Remove-Item -LiteralPath $skinZip -Force
[IO.File]::WriteAllText((Join-Path $skinSource 'manifest.json'), '{"id":"zip-unsigned","name":"Unsigned Production","css":"skin.css","assets":{}}', [Text.UTF8Encoding]::new($false))
Compress-Archive -Path (Join-Path $skinSource '*') -DestinationPath $skinZip
try { Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/skins/import" -Headers $headers -ContentType 'application/json' -Body (@{ path = $skinZip } | ConvertTo-Json) | Out-Null } catch { $unsignedRejected = $true }
$incompatibleRejected = $false
Remove-Item -LiteralPath $skinZip -Force
[IO.File]::WriteAllText((Join-Path $skinSource 'manifest.json'), '{"id":"zip-incompatible","name":"Incompatible Development","css":"skin.css","assets":{},"development":true,"maxAppVersion":"6.0.0"}', [Text.UTF8Encoding]::new($false))
Compress-Archive -Path (Join-Path $skinSource '*') -DestinationPath $skinZip
try { Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/skins/import" -Headers $headers -ContentType 'application/json' -Body (@{ path = $skinZip } | ConvertTo-Json) | Out-Null } catch { $incompatibleRejected = $true }
[IO.Directory]::Delete($skinFixture, $true)
$extensionFixture = Join-Path ([IO.Path]::GetTempPath()) ('claude-extension-smoke-' + [guid]::NewGuid().ToString('N'))
$extensionSource = Join-Path $extensionFixture 'source'
$extensionZip = Join-Path $extensionFixture 'skill.zip'
[IO.Directory]::CreateDirectory($extensionSource) | Out-Null
[IO.File]::WriteAllText((Join-Path $extensionSource 'manifest.json'), '{"schemaVersion":1,"type":"skill","id":"smoke-skill","name":"Smoke Skill","version":"1.0.0","development":true,"minAppVersion":"6.1.0","maxAppVersion":"6.9.0"}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $extensionSource 'SKILL.md'), "---`nname: smoke-skill`ndescription: offline smoke fixture`n---`n`n# Smoke Skill`n", [Text.UTF8Encoding]::new($false))
Compress-Archive -Path (Join-Path $extensionSource '*') -DestinationPath $extensionZip
$extensionImport = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/extensions/import" -Headers $headers -ContentType 'application/json' -Body (@{ workspace = $Workspace; path = $extensionZip } | ConvertTo-Json)
$extensionList = Invoke-RestMethod -Uri "$baseUrl/api/workbench/extensions?workspace=$([uri]::EscapeDataString($Workspace))" -Headers $headers
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/extensions/delete" -Headers $headers -ContentType 'application/json' -Body (@{ workspace = $Workspace; type = 'skill'; name = 'smoke-skill' } | ConvertTo-Json) | Out-Null
[IO.Directory]::Delete($extensionFixture, $true)
$approval = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/permissions/request" -Headers $headers -ContentType 'application/json' -Body (@{ jobId = 'native-smoke'; toolName = 'Read'; input = @{ file_path = 'smoke-only' } } | ConvertTo-Json)
$pendingApprovals = @(Invoke-RestMethod -Uri "$baseUrl/api/permissions/pending" -Headers $headers)
$approvalResult = Invoke-RestMethod -Uri "$baseUrl/api/permissions/result/$($approval.id)" -Headers $headers
$updateConfig = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/update/config" -Headers $headers -ContentType 'application/json' -Body (@{ channel = 'stable'; manifestUrl = ''; allowUnsignedPreview = $false } | ConvertTo-Json)
$updateCheck = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/update/check" -Headers $headers -ContentType 'application/json' -Body '{}'
$insecureManifestRejected = $false
try { Invoke-RestMethod -Method Post -Uri "$baseUrl/api/workbench/update/config" -Headers $headers -ContentType 'application/json' -Body (@{ channel = 'stable'; manifestUrl = 'http://example.invalid/release.json' } | ConvertTo-Json) | Out-Null } catch { $insecureManifestRejected = $true }

Assert-True ($bootstrap.version -eq '6.4.21-dev.native.local') 'version mismatch'
Assert-True ($bootstrap.edition.id -eq 'local' -and -not [bool]$bootstrap.edition.openSource) 'local edition metadata mismatch'
Assert-True ($bootstrap.backend -eq 'C#/.NET native host') 'backend type mismatch'
Assert-True ([bool]($bootstrap.trayMode)) 'tray mode is disabled'
Assert-True ($projects.Count -gt 0) 'project list is empty'
Assert-True ($null -ne $tree.entries) 'file tree contract mismatch'
Assert-True ($null -ne $extensions.skills) 'extensions contract mismatch'
Assert-True ($null -ne $usage.total) 'usage contract mismatch'
Assert-True ([bool]($health.claudeExists)) 'Claude Code CLI is missing'
Assert-True ($health.terminalRuntime -eq 'Windows ConPTY') 'ConPTY runtime is not active'
Assert-True ([bool]($reliability.ok)) 'reliability self-test failed'
Assert-True ($null -ne $schedules) 'schedule contract mismatch'
Assert-True ($terminal.exitCode -eq 0 -and $terminal.output -match 'CLAUDE_WORKBENCH_SMOKE_OK') 'embedded terminal failed'
Assert-True ($conptyOutput -match 'CLAUDE_CONPTY_OK') 'ConPTY roundtrip failed'
Assert-True ($skinImport.id -eq 'zip-smoke') 'ZIP skin import failed'
Assert-True ($unsignedRejected) 'unsigned production skin was accepted'
Assert-True ($incompatibleRejected) 'incompatible skin was accepted'
Assert-True ($extensionImport.type -eq 'skill' -and $extensionImport.signatureStatus -eq 'unsigned-development') 'development Skill package import failed'
Assert-True (@($extensionList.skills | Where-Object { $_.name -eq 'smoke-skill' -and $_.signatureStatus -eq 'unsigned-development' }).Count -eq 1) 'Skill package trust metadata missing'
Assert-True ([bool]$approval.automatic -and @($pendingApprovals | Where-Object { $_.id -eq $approval.id }).Count -eq 0) 'missing task manifest was not denied automatically'
Assert-True ($approvalResult.decision.behavior -eq 'deny') 'permission policy decision roundtrip failed'
Assert-True ($updateConfig.channel -eq 'stable' -and -not $updateCheck.configured) 'update channel state failed'
Assert-True ($insecureManifestRejected) 'insecure update manifest URL was accepted'

[pscustomobject]@{
    Version = $bootstrap.version
    Backend = $bootstrap.backend
    Pid = $runtime.pid
    Port = $runtime.port
    Projects = $projects.Count
    Transcripts = $transcripts.Count
    GitAvailable = $git.branch -ne $null
    Skills = @($extensions.skills).Count
    Agents = @($extensions.agents).Count
    UsageTotal = $usage.total
    ClaudeExists = $health.claudeExists
    Schedules = $schedules.Count
    Terminal = 'OK'
    ConPTY = 'OK'
    ReliabilityChecks = @($reliability.checks).Count
    ZipSkin = 'OK'
    SkinSignaturePolicy = 'OK'
    ExtensionPackagePolicy = 'OK'
    UpdateChannelPolicy = 'OK'
    PermissionBroker = 'OK'
} | Format-List

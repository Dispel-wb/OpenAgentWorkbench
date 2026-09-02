param(
    [Parameter(Mandatory = $true)][string]$RuntimePath,
    [int]$UiPid = 0,
    [int]$Iterations = 120,
    [string]$Output = '',
    [switch]$InjectRendererCrash,
    [ValidateRange(0, 8)][int]$RendererCrashCount = 0
)

$ErrorActionPreference = 'Stop'
if ($Iterations -lt 10 -or $Iterations -gt 1000) { throw 'Iterations must be in [10, 1000]' }
$runtimePath = [IO.Path]::GetFullPath($RuntimePath)
if (-not (Test-Path -LiteralPath $runtimePath)) { throw "Runtime state not found: $runtimePath" }
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $PSScriptRoot 'ui-resilience-final.png'
}
$Output = [IO.Path]::GetFullPath($Output)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$nativeSource = [string]::Join([Environment]::NewLine, @(
    'using System;',
    'using System.Runtime.InteropServices;',
    'public static class UiResilienceNative {',
    '    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }',
    '    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);',
    '    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);',
    '    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);',
    '    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);',
    '    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);',
    '    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);',
    '}'
))
Add-Type $nativeSource

function Read-Runtime {
    Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Wait-Until([scriptblock]$Action, [scriptblock]$Predicate, [int]$Seconds = 20, [string]$Message = 'condition') {
    $expires = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Milliseconds 150
        try { $value = & $Action } catch { $value = $null }
        if (& $Predicate $value) { return $value }
    } while ((Get-Date) -lt $expires)
    throw "Timed out waiting for $Message"
}

function Find-UiProcess {
    if ($UiPid -gt 0) {
        $candidate = Get-Process -Id $UiPid -ErrorAction Stop
        if ($candidate.MainWindowHandle -eq 0) { throw "UI process $UiPid has no main window" }
        return $candidate
    }
    $candidate = Get-Process ClaudeCodeWorkbench -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 -and -not [string]::IsNullOrWhiteSpace($_.MainWindowTitle) } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) { throw 'Workbench UI window not found' }
    return $candidate
}

function Get-DescendantProcesses([int]$RootPid) {
    $all = @(Get-CimInstance Win32_Process)
    $known = New-Object 'System.Collections.Generic.HashSet[int]'
    [void]$known.Add($RootPid)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($item in $all) {
            if ($known.Contains([int]$item.ParentProcessId) -and -not $known.Contains([int]$item.ProcessId)) {
                [void]$known.Add([int]$item.ProcessId)
                $changed = $true
            }
        }
    }
    @($all | Where-Object { $_.ProcessId -ne $RootPid -and $known.Contains([int]$_.ProcessId) })
}

function Capture-AndCheck([IntPtr]$Handle, [string]$Path) {
    $rect = [UiResilienceNative+RECT]::new()
    if (-not [UiResilienceNative]::GetWindowRect($Handle, [ref]$rect)) { throw 'GetWindowRect failed' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 900 -or $height -lt 600) { throw "Unexpected UI bounds: ${width}x${height}" }
    $bitmap = New-Object Drawing.Bitmap $width, $height
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [UiResilienceNative]::PrintWindow($Handle, $hdc, 2)) { throw 'PrintWindow failed' }
    } finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }
    $sampleCount = 0
    $nearWhite = 0
    $sum = 0.0
    $sumSquares = 0.0
    $buckets = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($y = 8; $y -lt $height; $y += 16) {
        for ($x = 8; $x -lt $width; $x += 16) {
            $color = $bitmap.GetPixel($x, $y)
            $luma = 0.2126 * $color.R + 0.7152 * $color.G + 0.0722 * $color.B
            $sampleCount++
            if ($luma -ge 245) { $nearWhite++ }
            $sum += $luma
            $sumSquares += $luma * $luma
            [void]$buckets.Add((($color.R -shr 4) -shl 8) -bor (($color.G -shr 4) -shl 4) -bor ($color.B -shr 4))
        }
    }
    $mean = $sum / [Math]::Max(1, $sampleCount)
    $variance = $sumSquares / [Math]::Max(1, $sampleCount) - $mean * $mean
    $whiteRatio = $nearWhite / [double][Math]::Max(1, $sampleCount)
    $directory = Split-Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    if ($whiteRatio -gt 0.94) { throw "White-screen signature detected: $([Math]::Round($whiteRatio * 100, 2))% near-white" }
    if ($buckets.Count -lt 8 -or $variance -lt 12) { throw "Uniform-frame signature detected: buckets=$($buckets.Count), variance=$([Math]::Round($variance, 2))" }
    [pscustomobject]@{
        Width = $width
        Height = $height
        NearWhitePercent = [Math]::Round($whiteRatio * 100, 2)
        ColorBuckets = $buckets.Count
        LumaVariance = [Math]::Round($variance, 2)
        Path = $Path
    }
}

$runtimeBefore = Read-Runtime
if ($runtimeBefore.state -ne 'running') { throw 'Host runtime is not running' }
$hostPidBefore = [int]$runtimeBefore.pid
$hostBefore = Get-Process -Id $hostPidBefore -ErrorAction Stop
$ui = Find-UiProcess
$uiPidBefore = $ui.Id
$handle = [IntPtr]$ui.MainWindowHandle
$hostStart = $hostBefore.StartTime
$uiStart = $ui.StartTime

[UiResilienceNative]::ShowWindow($handle, 9) | Out-Null
[UiResilienceNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 300

$sizes = @(
    @(920, 620), @(1000, 680), @(1240, 780), @(960, 720),
    @(1360, 860), @(1080, 700), @(1280, 760)
)
$clicks = @(@(520, 250), @(650, 410), @(760, 550), @(430, 330))
$WM_LBUTTONDOWN = 0x0201
$WM_LBUTTONUP = 0x0202
$SWP_NOMOVE = 0x0002
$SWP_NOZORDER = 0x0004
$SWP_NOACTIVATE = 0x0010
$zoomBursts = 0

function Send-ZoomBurst([IntPtr]$Handle) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            [UiResilienceNative]::SetForegroundWindow($Handle) | Out-Null
            [System.Windows.Forms.SendKeys]::SendWait('^{ADD}')
            [System.Windows.Forms.SendKeys]::SendWait('^{SUBTRACT}')
            [System.Windows.Forms.SendKeys]::SendWait('^0')
            return $true
        }
        catch {
            $baseError = $_.Exception.GetBaseException()
            if (-not ($baseError -is [ComponentModel.Win32Exception] -and $baseError.NativeErrorCode -eq 0)) { throw }
            Start-Sleep -Milliseconds 80
        }
    }
    return $false
}

for ($index = 0; $index -lt $Iterations; $index++) {
    $size = $sizes[$index % $sizes.Count]
    if (-not [UiResilienceNative]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, $size[0], $size[1], $SWP_NOMOVE -bor $SWP_NOZORDER -bor $SWP_NOACTIVATE)) {
        throw "SetWindowPos failed at iteration $index"
    }
    $point = $clicks[$index % $clicks.Count]
    $lParam = [IntPtr](($point[1] -shl 16) -bor ($point[0] -band 0xffff))
    [UiResilienceNative]::PostMessage($handle, $WM_LBUTTONDOWN, [IntPtr]1, $lParam) | Out-Null
    [UiResilienceNative]::PostMessage($handle, $WM_LBUTTONUP, [IntPtr]::Zero, $lParam) | Out-Null
    if (($index % 12) -eq 0) {
        if (Send-ZoomBurst $handle) { $zoomBursts++ }
    }
    if (($index % 20) -eq 19) {
        [UiResilienceNative]::ShowWindow($handle, 6) | Out-Null
        Start-Sleep -Milliseconds 80
        [UiResilienceNative]::ShowWindow($handle, 9) | Out-Null
    }
    Start-Sleep -Milliseconds 22
}
if ($zoomBursts -lt 1) { throw 'Zoom stress input could not be delivered to the Workbench window' }

$requestedRendererCrashes = if ($RendererCrashCount -gt 0) { $RendererCrashCount } elseif ($InjectRendererCrash) { 1 } else { 0 }
$rendererCrashRecovered = $false
$rendererCrashesRecovered = 0
$rendererBefore = $null
$rendererAfter = $null
for ($crashIndex = 0; $crashIndex -lt $requestedRendererCrashes; $crashIndex++) {
    $rendererBefore = Get-DescendantProcesses $uiPidBefore |
        Where-Object { $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -match '--type=renderer' } |
        Select-Object -First 1
    if ($null -eq $rendererBefore) { throw "Workbench WebView2 renderer not found before crash $($crashIndex + 1)" }
    Stop-Process -Id ([int]$rendererBefore.ProcessId) -Force
    $rendererAfter = Wait-Until {
        Get-DescendantProcesses $uiPidBefore |
            Where-Object { $_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -match '--type=renderer' -and [int]$_.ProcessId -ne [int]$rendererBefore.ProcessId } |
            Select-Object -First 1
    } { param($value) $null -ne $value } 25 "WebView2 renderer recovery $($crashIndex + 1)"
    Start-Sleep -Milliseconds 1700
    $ui.Refresh()
    if ($ui.HasExited -or -not $ui.Responding) { throw "UI failed after renderer crash $($crashIndex + 1)" }
    $intermediate = [IO.Path]::Combine([IO.Path]::GetDirectoryName($Output),
        [IO.Path]::GetFileNameWithoutExtension($Output) + "-recovery-$($crashIndex + 1).png")
    Capture-AndCheck $handle $intermediate | Out-Null
    $rendererCrashesRecovered++
}
$rendererCrashRecovered = $rendererCrashesRecovered -eq $requestedRendererCrashes -and $requestedRendererCrashes -gt 0

$ui.Refresh()
if ($ui.HasExited -or -not $ui.Responding) { throw 'UI process exited or stopped responding' }
if ($ui.Id -ne $uiPidBefore -or $ui.StartTime -ne $uiStart) { throw 'UI process was replaced during resilience test' }
$runtimeAfter = Read-Runtime
if ($runtimeAfter.state -ne 'running' -or [int]$runtimeAfter.pid -ne $hostPidBefore) { throw 'Host PID/state changed during UI resilience test' }
$hostAfter = Get-Process -Id $hostPidBefore -ErrorAction Stop
if ($hostAfter.StartTime -ne $hostStart) { throw 'Host process was replaced during UI resilience test' }

Add-Type -AssemblyName System.Security
$secret = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
    [Convert]::FromBase64String([string]$runtimeAfter.authProtected), $null,
    [Security.Cryptography.DataProtectionScope]::CurrentUser))
$headers = @{'X-Desktop-Secret' = $secret; 'X-Workbench-Protocol' = '2'}
$base = "http://127.0.0.1:$($runtimeAfter.port)"
$bootstrap = Invoke-RestMethod -Uri "$base/api/bootstrap" -Headers $headers -TimeoutSec 5
$health = Invoke-RestMethod -Uri "$base/api/workbench/health?workspace=$([uri]::EscapeDataString((Split-Path (Split-Path $runtimePath -Parent) -Parent)))" -Headers $headers -TimeoutSec 5
if ($bootstrap.persistence.integrity -ne 'ok' -or -not $health.durableJobState) { throw 'Host health invariant failed after UI stress' }

[UiResilienceNative]::ShowWindow($handle, 9) | Out-Null
[UiResilienceNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 500
$frame = Capture-AndCheck $handle $Output

[pscustomobject]@{
    UiResilience = 'PASS'
    Iterations = $Iterations
    UiPid = $uiPidBefore
    UiPidUnchanged = $true
    HostPid = $hostPidBefore
    HostPidUnchanged = $true
    RendererCrashInjected = $requestedRendererCrashes -gt 0
    RendererCrashCount = $requestedRendererCrashes
    RendererCrashesRecovered = $rendererCrashesRecovered
    RendererBefore = if ($rendererBefore) { [int]$rendererBefore.ProcessId } else { $null }
    RendererAfter = if ($rendererAfter) { [int]$rendererAfter.ProcessId } else { $null }
    RendererCrashRecovered = $rendererCrashRecovered
    SQLiteIntegrity = $bootstrap.persistence.integrity
    DurableJobState = [bool]$health.durableJobState
    Screenshot = $frame.Path
    FrameColorBuckets = $frame.ColorBuckets
    FrameNearWhitePercent = $frame.NearWhitePercent
} | Format-List

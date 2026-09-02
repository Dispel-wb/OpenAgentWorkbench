param([Parameter(Mandatory=$true)][string]$Output,[string]$ProcessName='ClaudeCodeWorkbench')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCaptureNative {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int command);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
}
'@
[WindowCaptureNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$process=Get-Process $ProcessName -ErrorAction SilentlyContinue |
  Where-Object { $_.MainWindowHandle -ne 0 -and -not [string]::IsNullOrWhiteSpace($_.MainWindowTitle) } |
  Sort-Object StartTime -Descending |
  Select-Object -First 1
$handle=if($process){$process.MainWindowHandle}else{[IntPtr]::Zero};if($handle -eq [IntPtr]::Zero){throw 'Workbench window not found'}
$handle=[IntPtr]$handle
if([WindowCaptureNative]::IsIconic($handle)){[WindowCaptureNative]::ShowWindow($handle,9)|Out-Null;Start-Sleep -Milliseconds 300}
$rect=[WindowCaptureNative+RECT]::new();if(-not[WindowCaptureNative]::GetWindowRect($handle,[ref]$rect)){throw 'GetWindowRect failed'}
$width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top;if($width -lt 100 -or $height -lt 100){throw "Workbench window bounds invalid: ${width}x${height}"}
[WindowCaptureNative]::SetForegroundWindow($handle)|Out-Null;Start-Sleep -Milliseconds 250
$bitmap=New-Object Drawing.Bitmap $width,$height;$graphics=[Drawing.Graphics]::FromImage($bitmap);$hdc=$graphics.GetHdc()
try{if(-not[WindowCaptureNative]::PrintWindow($handle,$hdc,2)){throw 'PrintWindow failed'}}finally{$graphics.ReleaseHdc($hdc);$graphics.Dispose()}
$bitmap.Save($Output,[Drawing.Imaging.ImageFormat]::Png);$bitmap.Dispose()
[pscustomobject]@{Path=$Output;Width=$width;Height=$height;Handle=$handle}|Format-List

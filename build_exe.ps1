param(
    [ValidateSet('Local','OpenSource')][string]$Edition = 'Local',
    [string]$VisualStudioRoot = '',
    [string]$WebViewLibraryRoot = '',
    [string]$NewtonsoftJsonPath = '',
    [string]$PythonPath = '',
    [string]$DependencyRoot = '',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$python = if ($PythonPath) { $PythonPath } elseif (Test-Path -LiteralPath (Join-Path $root '.venv\Scripts\python.exe')) { Join-Path $root '.venv\Scripts\python.exe' } else { (Get-Command python -ErrorAction Stop).Source }
$isOpenSource = $Edition -eq 'OpenSource'
$build = Join-Path $root 'build'
$asset = Join-Path $root 'static\assets\mascot.png'
$icon = Join-Path $root 'static\assets\claude-workbench.ico'
$native = Join-Path $root 'native'
$dist = Join-Path $root 'dist'
$defaultOutput = if ($isOpenSource) { Join-Path $dist 'opensource\OpenAgentWorkbench.exe' } else { Join-Path $dist 'ClaudeCodeWorkbench.exe' }
$output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { $defaultOutput } else { [IO.Path]::GetFullPath($OutputPath) }

$dependencyPath = if ($DependencyRoot) { [IO.Path]::GetFullPath($DependencyRoot) } elseif (Test-Path -LiteralPath (Join-Path $root '.packages')) { Join-Path $root '.packages' } else { '' }
$vsRoot = $VisualStudioRoot
if ($dependencyPath) {
    $csc = Join-Path $dependencyPath 'microsoft.net.compilers.toolset\4.14.0\tasks\net472\csc.exe'
    $newtonsoft = Join-Path $dependencyPath 'newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll'
    $webviewRoot = Join-Path $dependencyPath 'microsoft.web.webview2\1.0.3856.49'
} else {
    if (-not $vsRoot) { $vsRoot = $env:VSINSTALLDIR }
    if (-not $vsRoot) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) { $vsRoot = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1) }
    }
    if (-not $vsRoot) { throw 'Visual Studio with MSBuild/Roslyn was not found. Pass -VisualStudioRoot or restore locked dependencies.' }
    $csc = Join-Path $vsRoot 'MSBuild\Current\Bin\Roslyn\csc.exe'
    $newtonsoft = if ($NewtonsoftJsonPath) { $NewtonsoftJsonPath } else { Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\NuGet\Newtonsoft.Json.dll' }
    $webviewRoot = if ($WebViewLibraryRoot) { $WebViewLibraryRoot } elseif ($env:WEBVIEW2_LIBRARY_ROOT) { $env:WEBVIEW2_LIBRARY_ROOT } else { Join-Path $root '.venv\Lib\site-packages\webview\lib' }
}
$webviewCore = if ($dependencyPath) { Join-Path $webviewRoot 'lib\net462\Microsoft.Web.WebView2.Core.dll' } else { Join-Path $webviewRoot 'Microsoft.Web.WebView2.Core.dll' }
$webviewForms = if ($dependencyPath) { Join-Path $webviewRoot 'lib\net462\Microsoft.Web.WebView2.WinForms.dll' } else { Join-Path $webviewRoot 'Microsoft.Web.WebView2.WinForms.dll' }
$webviewLoader = Join-Path $webviewRoot 'runtimes\win-x64\native\WebView2Loader.dll'

foreach ($required in @($python, $csc, $newtonsoft, $webviewCore, $webviewForms, $webviewLoader)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing build dependency: $required" }
}

New-Item -ItemType Directory -Path $build -Force | Out-Null
if ($isOpenSource) {
    $asset = Join-Path $build 'open-agent.png'
    $icon = Join-Path $build 'open-agent.ico'
    & $python -c "from PIL import Image,ImageDraw; p=Image.new('RGBA',(256,256),(0,0,0,0)); d=ImageDraw.Draw(p); d.polygon([(128,28),(228,128),(128,228),(28,128)],fill=(20,25,33,255),outline=(117,166,255,255),width=12); d.polygon([(128,72),(184,128),(128,184),(72,128)],outline=(117,166,255,255),width=9); d.ellipse((116,116,140,140),fill=(232,238,248,255)); p.save(r'$asset'); p.save(r'$icon',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])"
} else {
    & $python -c "from PIL import Image; p=Image.open(r'$asset').convert('RGBA'); box=p.getbbox(); p=p.crop(box); canvas=Image.new('RGBA',(256,256)); p.thumbnail((210,150),Image.Resampling.NEAREST); canvas.alpha_composite(p,((256-p.width)//2,(256-p.height)//2)); canvas.save(r'$icon',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])"
}
if ($LASTEXITCODE -ne 0) { throw "Icon build failed: $LASTEXITCODE" }

New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$arguments = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-', '/deterministic+', '/langversion:latest', '/utf8output',
    "/pathmap:$root=/_/src",
    "/out:$output", "/win32icon:$icon", "/win32manifest:$(Join-Path $native 'app.manifest')",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Net.Http.dll', '/reference:System.Security.dll',
    '/reference:System.IO.Compression.dll', '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Xml.dll', '/reference:System.Xml.Linq.dll',
    "/reference:$newtonsoft", "/reference:$webviewCore", "/reference:$webviewForms",
    "/resource:$newtonsoft,deps.Newtonsoft.Json.dll",
    "/resource:$webviewCore,deps.Microsoft.Web.WebView2.Core.dll",
    "/resource:$webviewForms,deps.Microsoft.Web.WebView2.WinForms.dll",
    "/resource:$webviewLoader,deps.WebView2Loader.dll"
)
if ($isOpenSource) { $arguments += '/define:OPEN_SOURCE' }

$resources = [ordered]@{
    (Join-Path $root 'static\index.html') = 'static.index.html'
    (Join-Path $root 'static\app.js') = 'static.app.js'
    (Join-Path $root 'static\styles.css') = 'static.styles.css'
    (Join-Path $root 'static\modules\ui-store.js') = 'static.modules.ui-store.js'
    (Join-Path $root 'static\modules\document-ui.js') = 'static.modules.document-ui.js'
    (Join-Path $root 'static\modules\workflow-ui.js') = 'static.modules.workflow-ui.js'
    (Join-Path $root 'static\modules\provider-catalog.js') = 'static.modules.provider-catalog.js'
    (Join-Path $root 'static\vendor\vue.global.prod.js') = 'static.vendor.vue.global.prod.js'
    (Join-Path $root 'static\vendor\xterm.js') = 'static.vendor.xterm.js'
    (Join-Path $root 'static\vendor\xterm.css') = 'static.vendor.xterm.css'
    (Join-Path $root 'static\vendor\xterm-addon-fit.js') = 'static.vendor.xterm-addon-fit.js'
    (Join-Path $root 'static\vendor\xterm-addon-search.js') = 'static.vendor.xterm-addon-search.js'
    $asset = 'static.assets.mascot.png'
    $icon = 'static.assets.claude-workbench.ico'
}
if (-not $isOpenSource) {
    $resources[(Join-Path $root 'static\assets\mascot-key.png')] = 'static.assets.mascot-key.png'
    $resources[(Join-Path $root 'static\assets\mascot-original.png')] = 'static.assets.mascot-original.png'
}
foreach ($entry in $resources.GetEnumerator()) {
    $arguments += "/resource:$($entry.Key),$($entry.Value)"
}
$arguments += Get-ChildItem -LiteralPath $native -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }

& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Native C# build failed: $LASTEXITCODE" }

$obsoleteWorker = Join-Path $dist 'ClaudeGUI.Worker.ps1'
if (Test-Path -LiteralPath $obsoleteWorker) { Remove-Item -LiteralPath $obsoleteWorker -Force }

$item = Get-Item -LiteralPath $output
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
Write-Host "Native EXE: $($item.FullName)"
Write-Host "Size: $([math]::Round($item.Length / 1MB, 2)) MB"
Write-Host "SHA256: $hash"
Write-Host "Edition: $Edition"

$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\native\WebViewApiRequestPolicy.cs')).Path
Add-Type -Path $source
$base='http://127.0.0.1:43123/'
$cases=@(
  @{name='exact origin';expected=$true;origin='http://127.0.0.1:43123';referer='';site='cross-site'},
  @{name='same-origin referer';expected=$true;origin='';referer='http://127.0.0.1:43123/static/app.js';site='cross-site'},
  @{name='browser same-origin signal';expected=$true;origin='';referer='';site='same-origin'},
  @{name='external origin';expected=$false;origin='https://example.invalid';referer='https://example.invalid/page';site='cross-site'},
  @{name='different loopback port';expected=$false;origin='http://127.0.0.1:43124';referer='';site='same-site'},
  @{name='opaque origin';expected=$false;origin='null';referer='';site='cross-site'},
  @{name='missing browser provenance';expected=$false;origin='';referer='';site=''},
  @{name='host suffix spoof';expected=$false;origin='http://127.0.0.1.evil.invalid:43123';referer='';site='cross-site'}
)
foreach($case in $cases){
  $actual=[ClaudeCodeWorkbench.WebViewApiRequestPolicy]::IsTrusted($base,$case.origin,$case.referer,$case.site)
  if($actual-ne$case.expected){throw "Origin policy failed: $($case.name); expected=$($case.expected), actual=$actual"}
}
$hostSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\native\NativeHost.cs') -Raw
if($hostSource-notmatch 'WebViewApiRequestPolicy\.IsTrusted'){throw 'NativeHost does not apply the origin policy'}
if($hostSource-notmatch 'RemoveHeader\("X-Desktop-Secret"\)'){throw 'Untrusted WebView requests do not strip the desktop secret'}
[pscustomobject]@{WebViewApiOriginPolicy='PASS';Cases=$cases.Count;ExternalFramesReceiveDesktopSecret=$false}

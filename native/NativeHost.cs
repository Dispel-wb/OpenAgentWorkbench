using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ClaudeCodeWorkbench
{
    internal sealed class NativeHost : Form
    {
        public const string WindowTitle = EditionInfo.ProductName;
        private readonly ApiServer _server;
        private readonly bool _hostOnly;
        private readonly Timer _resizeTimer;
        private readonly Timer _connectionTimer;
        private readonly Timer _watchdogTimer;
        private readonly HttpClient _hostProbeClient;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _backendStateItem;
        private readonly ToolStripMenuItem _backendToggleItem;
        private readonly ToolStripMenuItem _startupItem;
        private WebView2 _browser;
        private string _pendingUrl;
        private bool _recreating;
        private bool _recoveryScheduled;
        private bool _connectionCheckRunning;
        private bool _hostUnavailable;
        private bool _allowExit;
        private bool _trayHintShown;
        private int _recoveryCount;
        private int _hostProbeFailures;
        private DateTime _lastHostProbeUtc = DateTime.MinValue;
        private HostConnection _hostConnection;
        private string _webView2Mode = "uninitialized";
        private string _webView2Version = "";
        private string _webView2RuntimeFolder = "";
        private Panel _recoveryPanel;
        private Label _recoveryTitle;
        private Label _recoveryMessage;

        public NativeHost(ApiServer server, bool hostOnly)
        {
            _server = server;
            _hostOnly = hostOnly;
            Text = hostOnly ? "Claude Code Agent Host" : WindowTitle;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 620);
            Size = new Size(1240, 780);
            BackColor = Color.FromArgb(22, 21, 20);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            _resizeTimer = new Timer { Interval = 90 };
            _connectionTimer = new Timer { Interval = 900 };
            _watchdogTimer = new Timer { Interval = 15000 };
            _hostProbeClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1400) };
            _resizeTimer.Tick += delegate
            {
                _resizeTimer.Stop();
                if (_browser != null && !_browser.IsDisposed) _browser.Bounds = ClientRectangle;
            };
            Resize += delegate
            {
                _resizeTimer.Stop();
                _resizeTimer.Start();
            };
            _connectionTimer.Tick += async delegate { await CheckHostConnectionAsync(); };
            if (!_hostOnly) _connectionTimer.Start();
            if (_server != null)
            {
                var menu = new ContextMenuStrip();
                var openItem = new ToolStripMenuItem("打开工作台");
                openItem.Font = new Font(openItem.Font, FontStyle.Bold);
                openItem.Click += delegate { ShowFromTray(); };
                _backendStateItem = new ToolStripMenuItem("后端：准备中") { Enabled = false };
                _backendToggleItem = new ToolStripMenuItem("停止后端");
                _backendToggleItem.Click += delegate { ToggleBackend(); };
                _startupItem = new ToolStripMenuItem("登录后自动启动后台");
                RefreshStartupItem();
                _startupItem.Click += delegate { ToggleStartup(); };
                var exitItem = new ToolStripMenuItem("完全退出");
                exitItem.Click += delegate { ExitApplication(); };
                menu.Items.Add(openItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(_backendStateItem);
                menu.Items.Add(_backendToggleItem);
                menu.Items.Add(_startupItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(exitItem);
                _tray = new NotifyIcon { Icon = Icon, Text = WindowTitle, Visible = true, ContextMenuStrip = menu };
                _tray.DoubleClick += delegate { ShowFromTray(); };
                _server.StateChanged += UpdateBackendState;
                _watchdogTimer.Tick += delegate { NativeHostWatchdog.EnsureRunning(); };
                _watchdogTimer.Start();
            }
            else
            {
                _backendStateItem = null;
                _backendToggleItem = null;
                _startupItem = null;
                _tray = null;
            }
            FormClosing += OnFormClosing;
            if (_hostOnly)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
                Opacity = 0;
                Shown += delegate { Hide(); };
            }
            else Shown += async delegate
            {
                if (!await CreateBrowserAsync()) ScheduleBrowserRecovery("WebView2 初始化失败，正在自动重试。");
            };
        }

        public void Navigate(string url)
        {
            _pendingUrl = url;
            if (_browser != null && _browser.CoreWebView2 != null) _browser.CoreWebView2.Navigate(url);
        }

        public void SetHostConnection(HostConnection connection)
        {
            _hostConnection = connection;
            if (connection != null) _pendingUrl = connection.Url;
        }

        private DialogResult ShowForegroundDialog(CommonDialog dialog)
        {
            var restoreTopMost = TopMost;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            Activate();
            BringToFront();
            try
            {
                TopMost = true;
                Activate();
                return dialog.ShowDialog(this);
            }
            finally
            {
                TopMost = restoreTopMost;
                Activate();
            }
        }

        public Task<string[]> SelectFilesAsync()
        {
            var source = new TaskCompletionSource<string[]>();
            BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dialog = new OpenFileDialog
                    {
                        Multiselect = true,
                        CheckFileExists = true,
                        Title = "选择要交给 Claude Code 的文件",
                        Filter = "所有文件 (*.*)|*.*"
                    })
                    {
                        source.SetResult(ShowForegroundDialog(dialog) == DialogResult.OK ? dialog.FileNames : new string[0]);
                    }
                }
                catch (Exception error) { source.SetException(error); }
            });
            return source.Task;
        }

        public Task<string> SelectClaudeExecutableAsync()
        {
            var source = new TaskCompletionSource<string>();
            BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dialog = new OpenFileDialog
                    {
                        Multiselect = false,
                        CheckFileExists = true,
                        Title = "选择 Claude Code 可执行文件",
                        Filter = "Claude Code (claude.exe)|claude.exe|Windows 可执行文件 (*.exe)|*.exe"
                    })
                    {
                        source.SetResult(ShowForegroundDialog(dialog) == DialogResult.OK ? dialog.FileName : "");
                    }
                }
                catch (Exception error) { source.SetException(error); }
            });
            return source.Task;
        }

        public Task<string> SelectSkinPackageAsync()
        {
            var source = new TaskCompletionSource<string>();
            BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dialog = new OpenFileDialog
                    {
                        Multiselect = false,
                        CheckFileExists = true,
                        Title = "导入 Agent 皮肤 ZIP 包",
                        Filter = "皮肤包 (*.zip)|*.zip"
                    })
                    {
                        source.SetResult(ShowForegroundDialog(dialog) == DialogResult.OK ? dialog.FileName : "");
                    }
                }
                catch (Exception error) { source.SetException(error); }
            });
            return source.Task;
        }

        public Task<string> SelectExtensionPackageAsync()
        {
            var source = new TaskCompletionSource<string>();
            BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dialog = new OpenFileDialog
                    {
                        Multiselect = false,
                        CheckFileExists = true,
                        Title = "导入签名 Skill / Subagent ZIP 包",
                        Filter = "扩展包 (*.zip)|*.zip"
                    })
                    {
                        source.SetResult(ShowForegroundDialog(dialog) == DialogResult.OK ? dialog.FileName : "");
                    }
                }
                catch (Exception error) { source.SetException(error); }
            });
            return source.Task;
        }

        public Task<string> SelectFolderAsync()
        {
            var source = new TaskCompletionSource<string>();
            BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dialog = new FolderBrowserDialog
                    {
                        Description = "选择 Claude Code 工作区",
                        ShowNewFolderButton = true,
                        SelectedPath = Directory.Exists(AppPaths.Workspace) ? AppPaths.Workspace : ""
                    })
                    {
                        source.SetResult(ShowForegroundDialog(dialog) == DialogResult.OK ? dialog.SelectedPath : "");
                    }
                }
                catch (Exception error) { source.SetException(error); }
            });
            return source.Task;
        }

        public void ShowNotification(string title, string message)
        {
            if (InvokeRequired) { BeginInvoke((Action<string, string>)ShowNotification, title, message); return; }
            if (_tray == null) return;
            _tray.ShowBalloonTip(3500, string.IsNullOrWhiteSpace(title) ? "Claude Code 工作台" : title,
                string.IsNullOrWhiteSpace(message) ? "任务状态已更新" : message, ToolTipIcon.Info);
        }

        public void ShowBackgroundNotification(string title, string message)
        {
            if (UiProcessAlive()) return;
            ShowNotification(title, message);
        }

        public void BeginUpdateExit(int delayMilliseconds)
        {
            if (InvokeRequired) { BeginInvoke((Action<int>)BeginUpdateExit, delayMilliseconds); return; }
            var timer = new Timer { Interval = Math.Max(200, Math.Min(5000, delayMilliseconds)) };
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                _allowExit = true;
                if (_tray != null) _tray.Visible = false;
                if (_server != null) _server.Stop(false);
                Close();
            };
            timer.Start();
        }

        private static bool UiProcessAlive()
        {
            try
            {
                var state = JsonUtil.Read(Path.Combine(AppPaths.Data, "ui-connection-state.json"), new Newtonsoft.Json.Linq.JObject()) as Newtonsoft.Json.Linq.JObject;
                var pid = (int?)state?["uiPid"] ?? 0;
                if (pid <= 0 || pid == Process.GetCurrentProcess().Id) return false;
                using (var process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch { return false; }
        }

        public Task<string> SaveTextAsync(string suggestedName, string text)
        {
            var source = new TaskCompletionSource<string>();
            BeginInvoke((Action)delegate
            {
                try
                {
                    using (var dialog = new SaveFileDialog
                    {
                        AddExtension = true,
                        DefaultExt = "txt",
                        FileName = string.IsNullOrWhiteSpace(suggestedName) ? "Claude-Code-对话.txt" : suggestedName,
                        Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                        OverwritePrompt = true,
                        Title = "导出对话为 TXT"
                    })
                    {
                        if (ShowForegroundDialog(dialog) != DialogResult.OK) { source.SetResult(""); return; }
                        File.WriteAllText(dialog.FileName, text ?? "", new System.Text.UTF8Encoding(true));
                        source.SetResult(dialog.FileName);
                    }
                }
                catch (Exception error) { source.SetException(error); }
            });
            return source.Task;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            CrashLog.Info("FormClosing reason=" + e.CloseReason + " allowExit=" + _allowExit);
            if (_server == null) return;
            if (_allowExit || e.CloseReason == CloseReason.WindowsShutDown) return;
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2200, "Claude Code 仍在运行", "窗口已隐藏到托盘，当前任务和后端不会停止。", ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            if (_hostOnly) { Program.LaunchUi(); return; }
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ToggleBackend()
        {
            if (_server == null) return;
            if (_server.IsRunning)
            {
                if (_server.HasActiveJobs && MessageBox.Show(this,
                    "当前还有任务正在运行。停止后端会同时停止这些任务，确定继续吗？",
                    "停止后端", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                _server.Stop(true);
            }
            else
            {
                try
                {
                    _server.Start();
                    Navigate(_server.BaseUrl);
                }
                catch (Exception error)
                {
                    CrashLog.Handled("RestartBackend", error);
                    MessageBox.Show(this, "后端启动失败：\n" + error.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ToggleStartup()
        {
            if (_startupItem == null) return;
            try
            {
                NativeStartupRegistration.SetEnabled(!_startupItem.Checked);
                RefreshStartupItem();
                _tray.ShowBalloonTip(1800, "后台启动设置", _startupItem.Checked ? "下次登录 Windows 后只启动 Agent Host 和托盘，不自动打开窗口。" : "已关闭登录后自动启动。", ToolTipIcon.Info);
            }
            catch (Exception error)
            {
                RefreshStartupItem();
                _tray.ShowBalloonTip(2200, "无法修改启动设置", SecretRedactor.Redact(error.Message), ToolTipIcon.Error);
            }
        }

        private void RefreshStartupItem()
        {
            if (_startupItem == null) return;
            try
            {
                var status = NativeStartupRegistration.Status();
                _startupItem.Checked = (bool?)status["enabled"] == true;
                _startupItem.Text = _startupItem.Checked ? "✓ 登录后自动启动后台" : "登录后自动启动后台";
            }
            catch { _startupItem.Checked = false; }
        }

        private void UpdateBackendState(bool running)
        {
            if (_server == null || _backendStateItem == null || _backendToggleItem == null) return;
            if (InvokeRequired) { BeginInvoke((Action<bool>)UpdateBackendState, running); return; }
            _backendStateItem.Text = running ? (_server.HasActiveJobs ? "后端：运行中 · 有活动任务" : "后端：运行中") : "后端：已停止";
            _backendToggleItem.Text = running ? "停止后端" : "启动后端";
        }

        private void ExitApplication()
        {
            if (_server == null) { Close(); return; }
            if (_server.HasActiveJobs && MessageBox.Show(this,
                "当前还有任务正在运行。完全退出会停止这些任务，确定退出吗？",
                "完全退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _allowExit = true;
            if (_tray != null) _tray.Visible = false;
            _server.Stop(true);
            Close();
        }

        private async Task CheckHostConnectionAsync()
        {
            if (_hostOnly || _connectionCheckRunning || IsDisposed) return;
            _connectionCheckRunning = true;
            try
            {
                HostConnection current; Newtonsoft.Json.Linq.JObject incompatible;
                if (!Program.TryReadHostConnection(out current, out incompatible) || current == null)
                {
                    _hostProbeFailures++;
                    if (_hostProbeFailures >= 2)
                    {
                        _hostUnavailable = true;
                        WriteUiConnectionState("reconnecting", null);
                        ShowRecoveryPanel("Agent Host 暂时不可用", "窗口仍在运行，正在等待本地 Host 恢复；后台任务状态不会在这里被判定为失败。", false);
                    }
                    return;
                }

                var changed = _hostConnection == null || current.Pid != _hostConnection.Pid ||
                    !string.Equals(current.Url, _hostConnection.Url, StringComparison.OrdinalIgnoreCase);
                if (changed)
                {
                    var reconnect = _hostConnection != null || _hostUnavailable;
                    SetHostConnection(current);
                    _hostProbeFailures = 0;
                    _hostUnavailable = false;
                    ShowRecoveryPanel("正在连接 Agent Host", "已找到本地 Host，正在恢复工作台和活动任务。", false);
                    if (reconnect) NativeMetrics.RecordUiReconnect();
                    Navigate(current.Url);
                    return;
                }

                if ((DateTime.UtcNow - _lastHostProbeUtc).TotalMilliseconds < 2400) return;
                _lastHostProbeUtc = DateTime.UtcNow;
                if (await ProbeHostAsync(current))
                {
                    _hostProbeFailures = 0;
                    if (_hostUnavailable)
                    {
                        _hostUnavailable = false;
                        NativeMetrics.RecordUiReconnect();
                        ShowRecoveryPanel("正在恢复工作台", "Agent Host 已重新响应，正在同步活动任务。", false);
                        Navigate(current.Url);
                    }
                }
                else
                {
                    _hostProbeFailures++;
                    if (_hostProbeFailures >= 2)
                    {
                        _hostUnavailable = true;
                        WriteUiConnectionState("reconnecting", current);
                        ShowRecoveryPanel("Agent Host 连接中断", "窗口和后台任务相互独立。工作台会持续重连，不会把暂时断连写成任务失败。", false);
                    }
                }
            }
            catch (Exception error) { CrashLog.Handled("HostConnectionCheck", error); }
            finally { _connectionCheckRunning = false; }
        }

        private async Task<bool> ProbeHostAsync(HostConnection connection)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, connection.Url + "api/bootstrap"))
                {
                    request.Headers.TryAddWithoutValidation("X-Desktop-Secret", connection.Secret);
                    request.Headers.TryAddWithoutValidation("X-Workbench-Protocol", connection.ProtocolVersion.ToString());
                    using (var response = await _hostProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                        return response.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }

        private void WriteUiConnectionState(string state, HostConnection connection)
        {
            try
            {
                JsonUtil.WriteAtomic(Path.Combine(AppPaths.Data, "ui-connection-state.json"), new Newtonsoft.Json.Linq.JObject
                {
                    ["state"] = state, ["uiPid"] = Process.GetCurrentProcess().Id,
                    ["hostPid"] = connection == null ? 0 : connection.Pid,
                    ["url"] = connection == null ? (_hostConnection == null ? "" : _hostConnection.Url) : connection.Url,
                    ["protocolVersion"] = connection == null ? ApiServer.ProtocolVersion : connection.ProtocolVersion,
                    ["webView2Mode"] = _webView2Mode,
                    ["webView2Version"] = _webView2Version,
                    ["webView2RuntimeFolder"] = _webView2RuntimeFolder,
                    ["windowsFamily"] = WebViewRuntimeInfo.WindowsFamily(),
                    ["windowsBuild"] = WebViewRuntimeInfo.WindowsBuild(),
                    ["updatedAt"] = ProviderStore.NowIso()
                });
            }
            catch { }
        }

        private async Task<bool> CreateBrowserAsync()
        {
            if (_recreating || IsDisposed) return false;
            _recreating = true;
            WebView2 next = null;
            try
            {
                var old = _browser;
                next = new WebView2
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(22, 21, 20),
                    DefaultBackgroundColor = Color.FromArgb(22, 21, 20),
                    Visible = false
                };
                Controls.Add(next);

                var profile = Path.Combine(AppPaths.Data, "native-webview-profile");
                Directory.CreateDirectory(profile);
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-features=CalculateNativeWinOcclusion,msEdgeSidebarV2 " +
                    "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                    "--disable-gpu-compositing --no-first-run");
                var runtimeFolder = WebViewRuntimeInfo.ResolveConfiguredFolder();
                _webView2Mode = WebViewRuntimeInfo.RuntimeMode(runtimeFolder);
                _webView2RuntimeFolder = runtimeFolder ?? "";
                var environment = await CoreWebView2Environment.CreateAsync(runtimeFolder, profile, options);
                _webView2Version = environment.BrowserVersionString ?? "";
                if (!_hostOnly) WriteUiConnectionState("webview-ready", _hostConnection);
                await next.EnsureCoreWebView2Async(environment);

                if (_hostConnection != null)
                {
                    next.CoreWebView2.AddWebResourceRequestedFilter(_hostConnection.Url + "api/*", CoreWebView2WebResourceContext.All);
                    next.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
                    {
                        var connection = _hostConnection;
                        if (connection == null || !e.Request.Uri.StartsWith(connection.Url + "api/", StringComparison.OrdinalIgnoreCase)) return;
                        e.Request.Headers.SetHeader("X-Desktop-Secret", connection.Secret);
                        e.Request.Headers.SetHeader("X-Workbench-Protocol", connection.ProtocolVersion.ToString());
                    };
                }

                var settings = next.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsBuiltInErrorPageEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;

                next.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    ChromeLauncher.Open(e.Uri);
                };
                next.CoreWebView2.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (!ReferenceEquals(_browser, next) || next.IsDisposed) return;
                    if (e.IsSuccess)
                    {
                        _recoveryCount = 0;
                        _hostProbeFailures = 0;
                        _hostUnavailable = false;
                        HideRecoveryPanel();
                        if (!_hostOnly && _hostConnection != null)
                            WriteUiConnectionState("connected", _hostConnection);
                        await ValidateBrowserContentAsync(next);
                    }
                    else
                    {
                        var error = new InvalidOperationException(e.WebErrorStatus.ToString());
                        CrashLog.Handled("Navigation", error);
                        ScheduleBrowserRecovery("页面导航失败：" + e.WebErrorStatus);
                    }
                };
                next.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    if (!ReferenceEquals(_browser, next)) return;
                    CrashLog.Handled("Renderer", new InvalidOperationException(e.ProcessFailedKind.ToString()));
                    BeginInvoke((Action)(delegate { ScheduleBrowserRecovery("界面渲染进程异常：" + e.ProcessFailedKind); }));
                };

                _browser = next;
                next.Visible = true;
                next.BringToFront();
                if (_recoveryPanel != null) _recoveryPanel.BringToFront();
                if (!string.IsNullOrWhiteSpace(_pendingUrl)) next.CoreWebView2.Navigate(_pendingUrl);
                if (old != null)
                {
                    Controls.Remove(old);
                    old.Dispose();
                }
                return true;
            }
            catch (Exception error)
            {
                CrashLog.Handled("WebView2", error);
                if (next != null && !ReferenceEquals(_browser, next))
                {
                    Controls.Remove(next);
                    next.Dispose();
                }
                ShowRecoveryPanel("界面渲染正在恢复", error.Message, true);
                return false;
            }
            finally { _recreating = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ApplyDarkTitleBar(Handle);
        }

        private async Task ValidateBrowserContentAsync(WebView2 browser)
        {
            try
            {
                await Task.Delay(650);
                if (IsDisposed || browser.IsDisposed || !ReferenceEquals(_browser, browser)) return;
                var result = await browser.CoreWebView2.ExecuteScriptAsync(
                    "Boolean(document.body && document.body.getBoundingClientRect().width > 400 && document.body.innerText.trim().length > 20)");
                if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
                    ScheduleBrowserRecovery("检测到空白界面，正在重新建立渲染环境。");
            }
            catch (Exception error)
            {
                CrashLog.Handled("RendererWatchdog", error);
                ScheduleBrowserRecovery("界面完整性检查失败，正在自动恢复。");
            }
        }

        private void ScheduleBrowserRecovery(string detail)
        {
            if (_recoveryScheduled || IsDisposed) return;
            _recoveryScheduled = true;
            BeginInvoke((Action)(async delegate
            {
                try
                {
                    _recoveryCount++;
                    if (_recoveryCount > 5)
                    {
                        ShowRecoveryPanel("界面暂时无法恢复", "自动恢复已连续失败。可以手动重新加载；后台任务仍由 Agent Host 独立运行。", true);
                        return;
                    }
                    ShowRecoveryPanel("界面渲染正在恢复", detail, false);
                    await Task.Delay(Math.Min(1800, 180 * (1 << Math.Min(3, _recoveryCount - 1))));
                    if (!await CreateBrowserAsync())
                    {
                        _recoveryScheduled = false;
                        ScheduleBrowserRecovery("WebView2 初始化失败，正在继续重试。");
                        return;
                    }
                    NativeMetrics.RecordUiReconnect();
                }
                finally { _recoveryScheduled = false; }
            }));
        }

        private void ShowRecoveryPanel(string titleText, string detail, bool manualRetry)
        {
            if (IsDisposed) return;
            if (_recoveryPanel != null)
            {
                _recoveryTitle.Text = titleText;
                _recoveryMessage.Text = detail;
                _recoveryPanel.Controls.OfType<Button>().First().Visible = manualRetry;
                _recoveryPanel.BringToFront();
                return;
            }
            _recoveryPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 21, 20) };
            _recoveryTitle = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 90,
                Padding = new Padding(34, 34, 20, 0),
                ForeColor = Color.FromArgb(238, 232, 226),
                Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold),
                Text = titleText
            };
            _recoveryMessage = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Padding = new Padding(36, 6, 36, 20),
                ForeColor = Color.FromArgb(155, 147, 139),
                Font = new Font("Microsoft YaHei UI", 10),
                Text = detail
            };
            var retry = new Button
            {
                Text = "重新加载界面",
                Width = 128,
                Height = 34,
                Left = 36,
                Top = 145,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(226, 128, 91),
                BackColor = Color.FromArgb(35, 31, 28),
                Visible = manualRetry
            };
            retry.Click += delegate { _recoveryCount = 0; HideRecoveryPanel(); ScheduleBrowserRecovery("正在手动重新加载界面。"); };
            _recoveryPanel.Controls.Add(retry);
            _recoveryPanel.Controls.Add(_recoveryMessage);
            _recoveryPanel.Controls.Add(_recoveryTitle);
            Controls.Add(_recoveryPanel);
            _recoveryPanel.BringToFront();
        }

        private void HideRecoveryPanel()
        {
            if (_recoveryPanel == null) return;
            Controls.Remove(_recoveryPanel);
            _recoveryPanel.Dispose();
            _recoveryPanel = null;
            _recoveryTitle = null;
            _recoveryMessage = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _resizeTimer.Dispose();
                _connectionTimer.Dispose();
                _watchdogTimer.Dispose();
                _hostProbeClient.Dispose();
                if (_server != null) _server.StateChanged -= UpdateBackendState;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_browser != null) _browser.Dispose();
                if (_recoveryPanel != null) _recoveryPanel.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class ChromeLauncher
    {
        public static void Open(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            };
            var chrome = candidates.FirstOrDefault(File.Exists);
            if (chrome != null) Process.Start(new ProcessStartInfo(chrome, "--new-tab " + Quote(url)) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _backendStateItem;
        private readonly ToolStripMenuItem _backendToggleItem;
        private WebView2 _browser;
        private string _pendingUrl;
        private bool _recreating;
        private bool _allowExit;
        private bool _trayHintShown;
        private int _recoveryCount;
        private HostConnection _hostConnection;

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
            _connectionTimer.Tick += delegate
            {
                if (_hostOnly) return;
                HostConnection current; Newtonsoft.Json.Linq.JObject incompatible;
                if (!Program.TryReadHostConnection(out current, out incompatible) || current == null) return;
                if (_hostConnection != null && current.Pid == _hostConnection.Pid && current.Url == _hostConnection.Url) return;
                SetHostConnection(current);
                Navigate(current.Url);
            };
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
                var exitItem = new ToolStripMenuItem("完全退出");
                exitItem.Click += delegate { ExitApplication(); };
                menu.Items.Add(openItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(_backendStateItem);
                menu.Items.Add(_backendToggleItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(exitItem);
                _tray = new NotifyIcon { Icon = Icon, Text = WindowTitle, Visible = true, ContextMenuStrip = menu };
                _tray.DoubleClick += delegate { ShowFromTray(); };
                _server.StateChanged += UpdateBackendState;
            }
            else
            {
                _backendStateItem = null;
                _backendToggleItem = null;
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
            else Shown += async delegate { await CreateBrowserAsync(); };
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
                        source.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : new string[0]);
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
                        source.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : "");
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
                        source.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : "");
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
                        source.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : "");
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
                        if (dialog.ShowDialog(this) != DialogResult.OK) { source.SetResult(""); return; }
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
                    CrashLog.Write("RestartBackend", error);
                    MessageBox.Show(this, "后端启动失败：\n" + error.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
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

        private async Task CreateBrowserAsync()
        {
            if (_recreating || IsDisposed) return;
            _recreating = true;
            try
            {
                var old = _browser;
                _browser = new WebView2
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(22, 21, 20),
                    DefaultBackgroundColor = Color.FromArgb(22, 21, 20)
                };
                Controls.Add(_browser);
                _browser.BringToFront();

                var profile = Path.Combine(AppPaths.Data, "native-webview-profile");
                Directory.CreateDirectory(profile);
                var options = new CoreWebView2EnvironmentOptions(
                    "--disable-features=CalculateNativeWinOcclusion,msEdgeSidebarV2 " +
                    "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                    "--disable-gpu-compositing --no-first-run");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile, options);
                await _browser.EnsureCoreWebView2Async(environment);

                if (_hostConnection != null)
                {
                    _browser.CoreWebView2.AddWebResourceRequestedFilter(_hostConnection.Url + "api/*", CoreWebView2WebResourceContext.All);
                    _browser.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
                    {
                        var connection = _hostConnection;
                        if (connection == null || !e.Request.Uri.StartsWith(connection.Url + "api/", StringComparison.OrdinalIgnoreCase)) return;
                        e.Request.Headers.SetHeader("X-Desktop-Secret", connection.Secret);
                        e.Request.Headers.SetHeader("X-Workbench-Protocol", connection.ProtocolVersion.ToString());
                    };
                }

                var settings = _browser.CoreWebView2.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreDevToolsEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsBuiltInErrorPageEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;

                _browser.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    ChromeLauncher.Open(e.Uri);
                };
                _browser.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    if (e.IsSuccess)
                    {
                        _recoveryCount = 0;
                        if (!_hostOnly && _hostConnection != null)
                            JsonUtil.WriteAtomic(Path.Combine(AppPaths.Data, "ui-connection-state.json"), new Newtonsoft.Json.Linq.JObject
                            {
                                ["state"] = "connected", ["uiPid"] = Process.GetCurrentProcess().Id,
                                ["hostPid"] = _hostConnection.Pid, ["url"] = _hostConnection.Url,
                                ["protocolVersion"] = _hostConnection.ProtocolVersion, ["connectedAt"] = ProviderStore.NowIso()
                            });
                    }
                    else CrashLog.Write("Navigation", new InvalidOperationException(e.WebErrorStatus.ToString()));
                };
                _browser.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e)
                {
                    CrashLog.Write("Renderer", new InvalidOperationException(e.ProcessFailedKind.ToString()));
                    BeginInvoke((Action)(async delegate { await RecoverBrowserAsync(); }));
                };

                if (!string.IsNullOrWhiteSpace(_pendingUrl)) _browser.CoreWebView2.Navigate(_pendingUrl);
                if (old != null)
                {
                    Controls.Remove(old);
                    old.Dispose();
                }
            }
            catch (Exception error)
            {
                CrashLog.Write("WebView2", error);
                ShowRecoveryPanel(error.Message);
            }
            finally { _recreating = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ApplyDarkTitleBar(Handle);
        }

        private async Task RecoverBrowserAsync()
        {
            if (_recreating || IsDisposed) return;
            _recoveryCount++;
            if (_recoveryCount > 3)
            {
                ShowRecoveryPanel("渲染进程连续恢复失败。请重新打开工作台，日志已保存在工作区配置目录。");
                return;
            }
            await Task.Delay(180 * _recoveryCount);
            await CreateBrowserAsync();
        }

        private void ShowRecoveryPanel(string detail)
        {
            if (IsDisposed) return;
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 21, 20) };
            var title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 90,
                Padding = new Padding(34, 34, 20, 0),
                ForeColor = Color.FromArgb(238, 232, 226),
                Font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold),
                Text = "界面渲染正在恢复"
            };
            var message = new Label
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
                BackColor = Color.FromArgb(35, 31, 28)
            };
            retry.Click += async delegate { Controls.Remove(panel); panel.Dispose(); _recoveryCount = 0; await CreateBrowserAsync(); };
            panel.Controls.Add(retry);
            panel.Controls.Add(message);
            panel.Controls.Add(title);
            Controls.Add(panel);
            panel.BringToFront();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _resizeTimer.Dispose();
                _connectionTimer.Dispose();
                if (_server != null) _server.StateChanged -= UpdateBackendState;
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_browser != null) _browser.Dispose();
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

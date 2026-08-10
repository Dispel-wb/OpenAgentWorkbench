using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: AssemblyTitle(ClaudeCodeWorkbench.EditionInfo.ProductName)]
[assembly: AssemblyProduct(ClaudeCodeWorkbench.EditionInfo.ProductName)]
[assembly: AssemblyVersion("6.3.0.0")]
[assembly: AssemblyFileVersion("6.3.0.0")]

namespace ClaudeCodeWorkbench
{
    internal static class Program
    {
        public const string AppContractVersion = EditionInfo.ContractVersion;
        private const string HostMutexName = "Local\\" + EditionInfo.StorageId + ".Host.V1";
        private const string UiMutexName = "Local\\" + EditionInfo.StorageId + ".UI.V1";
        private static Mutex _hostMutex;
        private static Mutex _uiMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            Bootstrap.InstallAssemblyResolver();
            if (args != null && args.Length >= 1 && string.Equals(args[0], "--install", StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.Initialize(); Environment.ExitCode = NativeInstaller.Install(args.Length >= 2 ? args[1] : null); return;
            }
            if (args != null && args.Length >= 1 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.Initialize(); Environment.ExitCode = NativeInstaller.BeginUninstall(args.Length >= 2 ? args[1] : null); return;
            }
            if (args != null && args.Length >= 3 && string.Equals(args[0], "--finish-uninstall", StringComparison.OrdinalIgnoreCase))
            {
                int parentPid; int.TryParse(args[2], out parentPid); Environment.ExitCode = NativeInstaller.FinishUninstall(args[1], parentPid); return;
            }
            if (args != null && args.Length >= 4 && string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.Initialize();
                Environment.ExitCode = NativeUpdater.Apply(args[1], args[2], args[3], !args.Any(value => string.Equals(value, "--no-launch", StringComparison.OrdinalIgnoreCase)));
                return;
            }
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--conpty-selftest", StringComparison.OrdinalIgnoreCase))
            {
                var workspace = args.Length >= 3 ? args[2] : Environment.CurrentDirectory;
                Environment.ExitCode = ConPtyTerminalSelfTest.Run(args[1], workspace) ? 0 : 1;
                return;
            }
            if (args != null && args.Length >= 3 && string.Equals(args[0], "--native-worker-selftest", StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.Initialize();
                Environment.ExitCode = NativeWorkerSelfTest.Run(args[1], args[2]);
                return;
            }
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--event-store-selftest", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("CLAUDE_GUI_WORKSPACE", args[1]);
                AppPaths.Initialize();
                Environment.ExitCode = AgentEventStoreSelfTest.Run();
                return;
            }
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--security-selftest", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = TaskSecuritySelfTest.Run(args[1]);
                return;
            }
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--workspace-selftest", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("CLAUDE_GUI_WORKSPACE", args[1]);
                AppPaths.Initialize();
                Environment.ExitCode = TaskWorkspaceSelfTest.Run(args[1]);
                return;
            }
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--skill-catalog-selftest", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = SkillCatalogSelfTest.Run(args[1]);
                return;
            }
            if (args != null && args.Any(value => string.Equals(value, "--permission-mcp", StringComparison.OrdinalIgnoreCase)))
            {
                PermissionMcp.Run();
                return;
            }

            Bootstrap.ExtractNativeLoader();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
            };
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                CrashLog.Write("UI", e.Exception);
            };

            try
            {
                AppPaths.Initialize();
                if (args != null && args.Any(value => string.Equals(value, "--host", StringComparison.OrdinalIgnoreCase))) RunHost();
                else RunUi();
            }
            catch (Exception error)
            {
                CrashLog.Write("Startup", error);
                MessageBox.Show(EditionInfo.ProductName + "启动失败：\n\n" + error.Message,
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void RunHost()
        {
            bool created;
            _hostMutex = new Mutex(true, HostMutexName, out created);
            if (!created) return;
            try
            {
                using (var server = new ApiServer())
                using (var trayHost = new NativeHost(server, true))
                {
                    server.AttachWindow(trayHost);
                    server.Start();
                    Application.Run(trayHost);
                }
            }
            finally { _hostMutex.ReleaseMutex(); _hostMutex.Dispose(); _hostMutex = null; }
        }

        private static void RunUi()
        {
            bool created;
            _uiMutex = new Mutex(true, UiMutexName, out created);
            if (!created) { NativeMethods.ShowExistingWindow(); return; }
            try
            {
                var connection = EnsureHost();
                using (var window = new NativeHost(null, false))
                {
                    window.SetHostConnection(connection);
                    window.Navigate(connection.Url);
                    Application.Run(window);
                }
            }
            finally { _uiMutex.ReleaseMutex(); _uiMutex.Dispose(); _uiMutex = null; }
        }

        internal static void LaunchUi()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--ui")
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppPaths.Workspace
                });
            }
            catch (Exception error) { CrashLog.Write("LaunchUi", error); }
        }

        private static HostConnection EnsureHost()
        {
            HostConnection connection;
            JObject incompatible;
            if (TryReadHostConnection(out connection, out incompatible)) return connection;
            if (incompatible != null)
            {
                var activeJobs = (int?)incompatible["activeJobs"] ?? 0;
                var pid = (int?)incompatible["pid"] ?? 0;
                if (Path.GetFileNameWithoutExtension(Application.ExecutablePath).EndsWith(".previous", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("旧版 previous 仅用于手动回滚，不能替换正在运行的新版 Host。");
                if (activeJobs > 0) throw new InvalidOperationException("旧版 Agent Host 仍有活动任务，无法安全替换。请等待任务结束后重新打开工作台。");
                try { using (var process = Process.GetProcessById(pid)) { process.Kill(); process.WaitForExit(8000); } }
                catch (Exception error) { throw new InvalidOperationException("无法安全替换不兼容的 Agent Host：" + error.Message); }
            }
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--host")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppPaths.Workspace
            });
            var expires = DateTime.UtcNow.AddSeconds(18);
            while (DateTime.UtcNow < expires)
            {
                Thread.Sleep(120);
                if (TryReadHostConnection(out connection, out incompatible)) return connection;
            }
            throw new InvalidOperationException("Agent Host 启动超时，请查看 native-runtime.log");
        }

        internal static bool TryReadHostConnection(out HostConnection connection, out JObject incompatible)
        {
            connection = null;
            incompatible = null;
            try
            {
                var file = Path.Combine(AppPaths.Data, "runtime-state.json");
                var state = JsonUtil.Read(file, new JObject()) as JObject ?? new JObject();
                var pid = (int?)state["pid"] ?? 0;
                var port = (int?)state["port"] ?? 0;
                if ((string)state["state"] != "running" || pid <= 0 || port <= 0) return false;
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return false;
                    var claimedPath = Path.GetFullPath((string)state["executablePath"] ?? "");
                    var actualPath = Path.GetFullPath(process.MainModule.FileName);
                    if (!string.Equals(claimedPath, actualPath, StringComparison.OrdinalIgnoreCase)) return false;
                }
                if ((int?)state["protocolVersion"] != ApiServer.ProtocolVersion)
                {
                    incompatible = state;
                    return false;
                }
                if (!string.Equals((string)state["appVersion"], AppContractVersion, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFullPath((string)state["executablePath"] ?? ""), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                {
                    incompatible = state;
                    return false;
                }
                var protectedSecret = (string)state["authProtected"] ?? "";
                if (protectedSecret.Length == 0) { incompatible = state; return false; }
                connection = new HostConnection
                {
                    Pid = pid, ProtocolVersion = ApiServer.ProtocolVersion,
                    Url = "http://127.0.0.1:" + port + "/", Secret = SecretStore.Unprotect(protectedSecret)
                };
                return true;
            }
            catch { return false; }
        }
    }

    internal sealed class HostConnection
    {
        public int Pid;
        public int ProtocolVersion;
        public string Url;
        public string Secret;
    }

    internal static class Bootstrap
    {
        private static readonly Dictionary<string, string> ManagedResources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Microsoft.Web.WebView2.Core", "deps.Microsoft.Web.WebView2.Core.dll" },
                { "Microsoft.Web.WebView2.WinForms", "deps.Microsoft.Web.WebView2.WinForms.dll" },
                { "Newtonsoft.Json", "deps.Newtonsoft.Json.dll" }
            };

        public static void InstallAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                var name = new AssemblyName(args.Name).Name;
                string resource;
                if (!ManagedResources.TryGetValue(name, out resource)) return null;
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (stream == null) return null;
                    var bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);
                    return Assembly.Load(bytes);
                }
            };
        }

        public static void ExtractNativeLoader()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                EditionInfo.StorageId, "native-1.0.3856.49-x64");
            Directory.CreateDirectory(root);
            var loader = Path.Combine(root, "WebView2Loader.dll");
            if (!File.Exists(loader) || new FileInfo(loader).Length < 100000)
            {
                using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("deps.WebView2Loader.dll"))
                using (var target = File.Create(loader))
                {
                    if (source == null) throw new InvalidOperationException("EXE 内缺少 WebView2Loader.dll");
                    source.CopyTo(target);
                }
            }
            NativeMethods.SetDllDirectory(root);
        }
    }

    internal static class AppPaths
    {
        public static string Workspace { get; private set; }
        public static string ClaudeRoot { get; private set; }
        public static string Data { get; private set; }
        public static string Runs { get; private set; }
        public static string Messages { get; private set; }
        public static string Images { get; private set; }
        public static string SessionsFile { get { return Path.Combine(Data, "sessions.json"); } }
        public static string SettingsFile { get { return Path.Combine(Data, "settings.json"); } }

        public static void Initialize()
        {
            Workspace = Environment.GetEnvironmentVariable("CLAUDE_GUI_WORKSPACE") ?? EditionInfo.DefaultWorkspace;
            ClaudeRoot = Environment.GetEnvironmentVariable("CLAUDE_GUI_ROOT") ?? EditionInfo.DefaultInstallDirectory;
            Data = Path.Combine(Workspace, ".claude-gui-v2");
            Runs = Path.Combine(Data, "runs");
            Messages = Path.Combine(Data, "messages");
            Images = Path.Combine(Workspace, "生成图片");
            foreach (var path in new[] { Workspace, Data, Runs, Messages, Images }) Directory.CreateDirectory(path);
        }
    }

    internal static class JsonUtil
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static JToken Read(string path, JToken fallback)
        {
            try { return JToken.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch { return fallback.DeepClone(); }
        }

        public static void WriteAtomic(string path, JToken value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, value.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public static string Compact(JToken value) { return value.ToString(Formatting.None); }
        public static JObject ObjectOrEmpty(JToken value) { return value as JObject ?? new JObject(); }
        public static JArray ArrayOrEmpty(JToken value) { return value as JArray ?? new JArray(); }
    }

    internal static class CrashLog
    {
        public static void Info(string message)
        {
            try
            {
                var root = AppPaths.Data ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EditionInfo.StorageId);
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "native-runtime.log"), DateTime.Now.ToString("s") + " " + SecretRedactor.Redact(message) + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static void Write(string stage, Exception error)
        {
            try
            {
                var root = AppPaths.Data ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EditionInfo.StorageId);
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "native-crash.log"),
                    DateTime.Now.ToString("s") + " [" + SecretRedactor.Redact(stage) + "] " + SecretRedactor.Redact(error == null ? "unknown" : error.ToString()) + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetDllDirectory(string path);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

        public static void ApplyDarkTitleBar(IntPtr handle)
        {
            var enabled = 1;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            var caption = 0x00171615;
            var border = 0x0034312e;
            var text = 0x00e4e9ee;
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }

        public static void ShowExistingWindow()
        {
            var handle = FindWindow(null, NativeHost.WindowTitle);
            if (handle == IntPtr.Zero) return;
            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class NativeInstaller
    {
        private const string ProductId = EditionInfo.ProductId;
        private const string ExeName = EditionInfo.ExecutableName;

        public static int Install(string requestedTarget)
        {
            try
            {
                var target = InstallTarget(requestedTarget); Directory.CreateDirectory(target);
                var source = Assembly.GetExecutingAssembly().Location; var destination = Path.Combine(target, ExeName);
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                {
                    var incoming = destination + ".incoming"; File.Copy(source, incoming, true);
                    if (!string.Equals(Sha256(source), Sha256(incoming), StringComparison.Ordinal)) throw new InvalidOperationException("安装文件 SHA-256 校验失败");
                    if (File.Exists(destination))
                    {
                        var previous = Path.Combine(target, Path.GetFileNameWithoutExtension(ExeName) + ".previous.exe");
                        File.Replace(incoming, destination, previous, true);
                    }
                    else File.Move(incoming, destination);
                }
                var manifest = new JObject
                {
                    ["productId"] = ProductId, ["version"] = Program.AppContractVersion, ["installedAt"] = ProviderStore.NowIso(),
                    ["executable"] = destination, ["sha256"] = Sha256(destination)
                };
                JsonUtil.WriteAtomic(Path.Combine(target, "install-manifest.json"), manifest);
                if (Environment.GetEnvironmentVariable("CLAUDE_GUI_INSTALL_TEST") != "1") { WriteStartMenuShortcut(destination); WriteUninstallRegistry(destination, target); }
                return 0;
            }
            catch (Exception error) { CrashLog.Handled("Install", error); return 81; }
        }

        public static int BeginUninstall(string requestedTarget)
        {
            try
            {
                NativeHostWatchdog.SignalStop();
                var target = InstallTarget(requestedTarget); VerifyInstallTarget(target);
                var helper = Path.Combine(Path.GetTempPath(), "ClaudeWorkbench-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, helper, true);
                Process.Start(new ProcessStartInfo(helper, "--finish-uninstall " + Quote(target) + " " + Process.GetCurrentProcess().Id)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath() });
                return 0;
            }
            catch (Exception error) { CrashLog.Handled("Uninstall", error); return 82; }
        }

        public static int FinishUninstall(string target, int parentPid)
        {
            try
            {
                VerifyInstallTarget(target);
                try { using (var parent = Process.GetProcessById(parentPid)) parent.WaitForExit(15000); } catch { }
                if (Environment.GetEnvironmentVariable("CLAUDE_GUI_INSTALL_TEST") != "1")
                {
                    try { NativeStartupRegistration.SetEnabled(false); } catch { }
                    RemoveStartMenuShortcut();
                    try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductId, false); } catch { }
                }
                Directory.Delete(Path.GetFullPath(target), true);
                return 0;
            }
            catch (Exception error) { CrashLog.Handled("FinishUninstall", error); return 83; }
        }

        private static string InstallTarget(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) value = Directory.Exists(@"D:\softwares") ? EditionInfo.DefaultInstallDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", EditionInfo.StorageId);
            var full = Path.GetFullPath(value);
            if (Path.GetPathRoot(full).TrimEnd('\\').Equals(full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能把磁盘根目录作为安装目录");
            return full;
        }

        private static void VerifyInstallTarget(string target)
        {
            var full = Path.GetFullPath(target); var manifestPath = Path.Combine(full, "install-manifest.json");
            if (!Directory.Exists(full) || !File.Exists(manifestPath)) throw new InvalidOperationException("目标不是当前工作台版本的安装目录");
            var manifest = JsonUtil.Read(manifestPath, new JObject()) as JObject ?? new JObject();
            if ((string)manifest["productId"] != ProductId) throw new InvalidOperationException("安装清单产品标识不匹配");
        }

        private static void WriteStartMenuShortcut(string executable)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", EditionInfo.ProductName);
            Directory.CreateDirectory(directory);
            var url = "[InternetShortcut]\r\nURL=file:///" + executable.Replace('\\', '/') + "\r\nIconFile=" + executable + "\r\nIconIndex=0\r\n";
            File.WriteAllText(Path.Combine(directory, EditionInfo.ProductName + ".url"), url, new UTF8Encoding(false));
        }

        private static void RemoveStartMenuShortcut()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", EditionInfo.ProductName);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static void WriteUninstallRegistry(string executable, string target)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductId))
            {
                key.SetValue("DisplayName", EditionInfo.ProductName); key.SetValue("DisplayVersion", Program.AppContractVersion);
                key.SetValue("Publisher", "Local User"); key.SetValue("DisplayIcon", executable);
                key.SetValue("InstallLocation", target); key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("UninstallString", Quote(executable) + " --uninstall " + Quote(target));
            }
        }

        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }
        private static string Sha256(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("X2"))); }
    }
}

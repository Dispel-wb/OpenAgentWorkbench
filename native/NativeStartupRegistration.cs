using System;
using System.Diagnostics;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class NativeStartupRegistration
    {
        private const string DefaultRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static string ValueName { get { return EditionInfo.ProductId + ".Host"; } }

        private static string RegistryPath
        {
            get
            {
                var requested = Environment.GetEnvironmentVariable("CLAUDE_GUI_STARTUP_REGISTRY_PATH") ?? "";
                if (string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_MODE"), "1", StringComparison.Ordinal) &&
                    requested.StartsWith(@"Software\ClaudeCodeWorkbench\Tests\", StringComparison.OrdinalIgnoreCase)) return requested;
                return DefaultRegistryPath;
            }
        }

        public static JObject Status()
        {
            var current = CurrentExecutable();
            var command = "";
            using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false)) command = Convert.ToString(key == null ? null : key.GetValue(ValueName, ""));
            var enabled = !string.IsNullOrWhiteSpace(command);
            var expected = Command(current);
            return new JObject
            {
                ["enabled"] = enabled,
                ["registered"] = enabled && string.Equals(command, expected, StringComparison.OrdinalIgnoreCase),
                ["scope"] = "current-user",
                ["launchMode"] = "host-only",
                ["currentExecutable"] = current,
                ["registeredExecutable"] = RegisteredExecutable(command)
            };
        }

        public static JObject SetEnabled(bool enabled)
        {
            if (enabled)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
                {
                    if (key == null) throw new InvalidOperationException("无法打开当前用户的登录启动配置");
                    key.SetValue(ValueName, Command(CurrentExecutable()), RegistryValueKind.String);
                }
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true)) if (key != null) key.DeleteValue(ValueName, false);
            }
            return Status();
        }

        public static void ReconcileExisting()
        {
            var status = Status();
            if ((bool?)status["enabled"] == true && (bool?)status["registered"] != true) SetEnabled(true);
        }

        private static string CurrentExecutable()
        {
            try { return System.IO.Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName); }
            catch { return System.IO.Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath); }
        }

        private static string Command(string executable)
        {
            return "\"" + (executable ?? "").Replace("\"", "") + "\" --host";
        }

        private static string RegisteredExecutable(string command)
        {
            command = (command ?? "").Trim();
            if (command.Length == 0) return "";
            if (command[0] == '"')
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : "";
            }
            var space = command.IndexOf(' ');
            return space > 0 ? command.Substring(0, space) : command;
        }
    }
}

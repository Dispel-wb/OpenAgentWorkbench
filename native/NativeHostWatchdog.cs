using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class NativeHostWatchdog
    {
        private static string MutexName { get { return "Local\\" + Program.ProcessScope + ".Watchdog.V1"; } }
        private static string StopEventName { get { return "Local\\" + Program.ProcessScope + ".Watchdog.Stop.V1"; } }
        private static string StatePath { get { return Path.Combine(AppPaths.Data, "watchdog-state.json"); } }
        private static string LogPath { get { return Path.Combine(AppPaths.Data, "watchdog.log"); } }

        public static bool Enabled
        {
            get
            {
                return !string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_MODE"), "1", StringComparison.Ordinal) ||
                    string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_WATCHDOG_TEST"), "1", StringComparison.Ordinal);
            }
        }

        public static void EnsureRunning()
        {
            if (!Enabled) return;
            try
            {
                var state = JsonUtil.Read(StatePath, new JObject()) as JObject ?? new JObject();
                var pid = (int?)state["pid"] ?? 0;
                DateTime heartbeat;
                var fresh = DateTime.TryParse((string)state["updatedAt"], out heartbeat) &&
                    DateTime.UtcNow - heartbeat.ToUniversalTime() < TimeSpan.FromSeconds(25);
                if (pid > 0 && fresh && IsProcess(pid, ApplicationPath())) return;
                Process.Start(new ProcessStartInfo(ApplicationPath(), "--watchdog")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppPaths.Workspace
                });
            }
            catch (Exception error) { AppendLog("Ensure", error.Message); }
        }

        public static void SignalStop()
        {
            try
            {
                using (var stop = EventWaitHandle.OpenExisting(StopEventName)) stop.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (Exception error) { AppendLog("SignalStop", error.Message); }
        }

        public static JObject Snapshot()
        {
            var state = JsonUtil.Read(StatePath, new JObject()) as JObject ?? new JObject();
            var pid = (int?)state["pid"] ?? 0;
            state["enabled"] = Enabled;
            state["alive"] = pid > 0 && IsProcess(pid, ApplicationPath());
            if (state["state"] == null) state["state"] = Enabled ? "starting" : "disabled-in-test";
            return state;
        }

        public static int Run()
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return 0;
                using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName))
                {
                    var restarts = 0;
                    var consecutiveFailures = 0;
                    var monitoredHostPid = 0;
                    var lastLaunchUtc = DateTime.MinValue;
                    var nextAttemptUtc = DateTime.MinValue;
                    var lastHeartbeatUtc = DateTime.MinValue;
                    WriteState("watching", monitoredHostPid, restarts, consecutiveFailures, "");
                    try
                    {
                        while (!stop.WaitOne(1000))
                        {
                            var runtime = JsonUtil.Read(Path.Combine(AppPaths.Data, "runtime-state.json"), new JObject()) as JObject ?? new JObject();
                            var runtimeState = (string)runtime["state"] ?? "";
                            var hostPid = (int?)runtime["pid"] ?? 0;
                            if (string.Equals(runtimeState, "stopped", StringComparison.OrdinalIgnoreCase))
                            {
                                WriteState("stopped-by-user", hostPid, restarts, consecutiveFailures, "Host 已按用户意图停止");
                                return 0;
                            }

                            if (string.Equals(runtimeState, "running", StringComparison.OrdinalIgnoreCase) && IsProcess(hostPid, ApplicationPath()))
                            {
                                monitoredHostPid = hostPid;
                                consecutiveFailures = 0;
                                lastLaunchUtc = DateTime.MinValue;
                                nextAttemptUtc = DateTime.MinValue;
                                if (DateTime.UtcNow - lastHeartbeatUtc >= TimeSpan.FromSeconds(5))
                                {
                                    WriteState("watching", hostPid, restarts, consecutiveFailures, "");
                                    lastHeartbeatUtc = DateTime.UtcNow;
                                }
                                continue;
                            }

                            if (!string.Equals(runtimeState, "running", StringComparison.OrdinalIgnoreCase))
                            {
                                if (DateTime.UtcNow - lastHeartbeatUtc >= TimeSpan.FromSeconds(5))
                                {
                                    WriteState("waiting-for-host", hostPid, restarts, consecutiveFailures, "Host 尚未发布运行状态");
                                    lastHeartbeatUtc = DateTime.UtcNow;
                                }
                                continue;
                            }

                            if (lastLaunchUtc != DateTime.MinValue && DateTime.UtcNow - lastLaunchUtc < TimeSpan.FromSeconds(20))
                            {
                                if (DateTime.UtcNow - lastHeartbeatUtc >= TimeSpan.FromSeconds(5))
                                {
                                    WriteState("restarting", monitoredHostPid, restarts, consecutiveFailures, "等待新 Host 就绪");
                                    lastHeartbeatUtc = DateTime.UtcNow;
                                }
                                continue;
                            }
                            if (DateTime.UtcNow < nextAttemptUtc) continue;

                            try
                            {
                                var process = Process.Start(new ProcessStartInfo(ApplicationPath(), "--host")
                                {
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    WindowStyle = ProcessWindowStyle.Hidden,
                                    WorkingDirectory = AppPaths.Workspace
                                });
                                restarts++;
                                consecutiveFailures++;
                                monitoredHostPid = process == null ? 0 : process.Id;
                                lastLaunchUtc = DateTime.UtcNow;
                                var backoffSeconds = Math.Min(60, Math.Max(2, 1 << Math.Min(5, consecutiveFailures)));
                                nextAttemptUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
                                WriteState("restarting", monitoredHostPid, restarts, consecutiveFailures, "检测到 Host 意外退出，已启动恢复");
                                AppendLog("Restart", "hostPid=" + hostPid + " newPid=" + monitoredHostPid + " attempt=" + restarts);
                            }
                            catch (Exception error)
                            {
                                consecutiveFailures++;
                                var backoffSeconds = Math.Min(60, Math.Max(2, 1 << Math.Min(5, consecutiveFailures)));
                                nextAttemptUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
                                WriteState("restart-failed", hostPid, restarts, consecutiveFailures, Limit(error.Message, 800));
                                AppendLog("RestartFailed", error.Message);
                            }
                        }
                        WriteState("stopped-for-maintenance", monitoredHostPid, restarts, consecutiveFailures, "Watchdog 已收到维护停止信号");
                        return 0;
                    }
                    catch (Exception error)
                    {
                        WriteState("failed", monitoredHostPid, restarts, consecutiveFailures, Limit(error.Message, 800));
                        AppendLog("Fatal", error.ToString());
                        return 91;
                    }
                    finally
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
        }

        private static void WriteState(string state, int hostPid, int restarts, int consecutiveFailures, string message)
        {
            try
            {
                JsonUtil.WriteAtomic(StatePath, new JObject
                {
                    ["pid"] = Process.GetCurrentProcess().Id,
                    ["state"] = state,
                    ["hostPid"] = hostPid,
                    ["restartCount"] = restarts,
                    ["consecutiveFailures"] = consecutiveFailures,
                    ["message"] = message ?? "",
                    ["executablePath"] = ApplicationPath(),
                    ["appVersion"] = Program.AppContractVersion,
                    ["updatedAt"] = ProviderStore.NowIso()
                });
            }
            catch (Exception error) { AppendLog("State", error.Message); }
        }

        private static bool IsProcess(int pid, string expectedPath)
        {
            if (pid <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited && string.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ApplicationPath() { return Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath); }
        private static string Limit(string value, int max) { value = value ?? ""; return value.Length <= max ? value : value.Substring(0, max); }

        private static void AppendLog(string stage, string message)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Data);
                var safe = (message ?? "").Replace("\r", " ").Replace("\n", " ");
                File.AppendAllText(LogPath, ProviderStore.NowIso() + " " + stage + " " + Limit(safe, 1600) + Environment.NewLine);
                var file = new FileInfo(LogPath);
                if (file.Length > 512 * 1024)
                {
                    var text = File.ReadAllText(LogPath);
                    File.WriteAllText(LogPath, text.Substring(Math.Max(0, text.Length - 256 * 1024)), new System.Text.UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}

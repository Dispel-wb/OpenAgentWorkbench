using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class NativeUpdater
    {
        public static int Apply(string stagedPath, string targetPath, string expectedSha256, bool launch, int parentPid = 0)
        {
            string previous = null;
            var replaced = false;
            try
            {
                stagedPath = Path.GetFullPath(stagedPath ?? "");
                targetPath = Path.GetFullPath(targetPath ?? "");
                if (!File.Exists(stagedPath)) throw new FileNotFoundException("Staged update does not exist", stagedPath);
                if (string.Equals(stagedPath, targetPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Staged and target paths must be different.");
                if (!string.Equals(Path.GetExtension(stagedPath), ".exe", StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Updater accepts EXE files only.");
                var actualHash = Sha256(stagedPath);
                if (!string.Equals(actualHash, (expectedSha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Staged update SHA-256 mismatch.");
                WriteStatus("applying", targetPath, actualHash, "正在等待旧版进程安全退出", "");
                NativeHostWatchdog.SignalStop();
                QuiesceTargetProcesses(targetPath, parentPid, TimeSpan.FromSeconds(8));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                var next = targetPath + ".next";
                File.Copy(stagedPath, next, true);
                if (!string.Equals(Sha256(next), actualHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Copied update failed SHA-256 verification.");
                previous = Path.Combine(Path.GetDirectoryName(targetPath), Path.GetFileNameWithoutExtension(targetPath) + ".previous.exe");
                if (File.Exists(targetPath)) File.Replace(next, targetPath, previous, true);
                else File.Move(next, targetPath);
                replaced = true;
                if (!string.Equals(Sha256(targetPath), actualHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed update failed SHA-256 verification.");
                if (launch)
                {
                    string launchError;
                    if (!LaunchAndVerifyHost(targetPath, actualHash, true, out launchError))
                    {
                        if (string.IsNullOrWhiteSpace(previous) || !File.Exists(previous)) throw new InvalidOperationException("New version failed startup verification and no previous version is available: " + launchError);
                        TerminateExactProcesses(targetPath);
                        RestorePrevious(previous, targetPath);
                        replaced = false;
                        string rollbackError;
                        if (!LaunchAndVerifyHost(targetPath, Sha256(targetPath), false, out rollbackError)) throw new InvalidOperationException("Update and automatic rollback both failed. New: " + launchError + "; rollback: " + rollbackError);
                        LaunchUi(targetPath);
                        WriteStatus("rolled-back", targetPath, Sha256(targetPath), "新版启动验证失败，已自动恢复上一版", launchError);
                        return 2;
                    }
                    LaunchUi(targetPath);
                }
                WriteStatus("installed", targetPath, actualHash, launch ? "更新完成，新版 Host 已通过启动验证" : "更新文件已安装", "");
                return 0;
            }
            catch (Exception error)
            {
                var recovered = false;
                var recoveryError = "";
                if (replaced && !string.IsNullOrWhiteSpace(previous) && File.Exists(previous))
                {
                    try { TerminateExactProcesses(targetPath); RestorePrevious(previous, targetPath); } catch { }
                }
                if (launch && !string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath) && !HasExactProcess(targetPath))
                {
                    recovered = LaunchAndVerifyHost(targetPath, SafeHash(targetPath), false, out recoveryError);
                    if (recovered) LaunchUi(targetPath);
                }
                WriteStatus("failed", targetPath, File.Exists(stagedPath ?? "") ? SafeHash(stagedPath) : "", recovered ? "更新失败，原版本已重新启动" : "更新失败", error.Message + (recoveryError.Length > 0 ? "; recovery: " + recoveryError : ""));
                try
                {
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EditionInfo.StorageId);
                    Directory.CreateDirectory(root);
                    File.AppendAllText(Path.Combine(root, "update-error.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
                }
                catch { }
                return 1;
            }
        }

        public static string Sha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path)) return string.Concat(algorithm.ComputeHash(stream).Select(value => value.ToString("X2")));
        }

        private static void QuiesceTargetProcesses(string targetPath, int parentPid, TimeSpan timeout)
        {
            if (parentPid > 0) WaitForProcessExit(parentPid, TimeSpan.FromSeconds(5));
            foreach (var process in ExactProcesses(targetPath))
            {
                try { process.CloseMainWindow(); } catch { }
                finally { process.Dispose(); }
            }
            var expires = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < expires)
            {
                var remaining = ExactProcesses(targetPath);
                if (remaining.Length == 0) return;
                foreach (var process in remaining) process.Dispose();
                Thread.Sleep(150);
            }
            TerminateExactProcesses(targetPath);
            var final = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < final)
            {
                var remaining = ExactProcesses(targetPath);
                if (remaining.Length == 0) return;
                foreach (var process in remaining) process.Dispose();
                Thread.Sleep(100);
            }
            throw new TimeoutException("Target application could not be stopped for update.");
        }

        private static Process[] ExactProcesses(string targetPath)
        {
            var matches = new System.Collections.Generic.List<Process>();
            foreach (var process in Process.GetProcesses())
            {
                var match = false;
                try { match = !process.HasExited && string.Equals(Path.GetFullPath(process.MainModule.FileName), targetPath, StringComparison.OrdinalIgnoreCase); }
                catch { }
                if (match) matches.Add(process); else process.Dispose();
            }
            return matches.ToArray();
        }

        private static void TerminateExactProcesses(string targetPath)
        {
            foreach (var process in ExactProcesses(targetPath))
            {
                try { process.Kill(); process.WaitForExit(3000); } catch { }
                finally { process.Dispose(); }
            }
        }

        private static bool HasExactProcess(string targetPath)
        {
            var processes = ExactProcesses(targetPath);
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private static void WaitForProcessExit(int pid, TimeSpan timeout)
        {
            try { using (var process = Process.GetProcessById(pid)) process.WaitForExit((int)timeout.TotalMilliseconds); }
            catch { }
        }

        private static bool LaunchAndVerifyHost(string targetPath, string expectedHash, bool allowInjectedFailure, out string error)
        {
            error = "";
            Process host = null;
            try
            {
                host = Process.Start(new ProcessStartInfo(targetPath, "--host")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = AppPaths.Workspace
                });
                if (host == null) { error = "Host process was not created"; return false; }
                var inject = allowInjectedFailure && string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_MODE"), "1", StringComparison.Ordinal) &&
                    string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_UPDATE_TEST_FAIL_NEW"), "1", StringComparison.Ordinal);
                var expires = DateTime.UtcNow.AddSeconds(inject ? 1 : 20);
                var runtimePath = Path.Combine(AppPaths.Data, "runtime-state.json");
                while (DateTime.UtcNow < expires)
                {
                    if (host.HasExited) { error = "Host exited with code " + host.ExitCode; return false; }
                    if (!inject)
                    {
                        var state = JsonUtil.Read(runtimePath, new JObject()) as JObject ?? new JObject();
                        if ((int?)state["pid"] == host.Id && string.Equals((string)state["state"], "running", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Sha256(targetPath), expectedHash, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    Thread.Sleep(150);
                }
                error = inject ? "Injected startup verification failure" : "Host did not publish a matching running state within 20 seconds";
                return false;
            }
            catch (Exception launchError) { error = launchError.Message; return false; }
            finally { if (host != null) host.Dispose(); }
        }

        private static void LaunchUi(string targetPath)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_MODE"), "1", StringComparison.Ordinal) &&
                string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_UPDATE_TEST_NO_UI"), "1", StringComparison.Ordinal)) return;
            Process.Start(new ProcessStartInfo(targetPath, "--ui") { UseShellExecute = false, WorkingDirectory = AppPaths.Workspace });
        }

        private static void RestorePrevious(string previous, string targetPath)
        {
            var rollback = targetPath + ".rollback";
            File.Copy(previous, rollback, true);
            if (File.Exists(targetPath)) File.Replace(rollback, targetPath, targetPath + ".failed", true);
            else File.Move(rollback, targetPath);
        }

        private static string SafeHash(string path)
        {
            try { return Sha256(path); } catch { return ""; }
        }

        private static void WriteStatus(string state, string targetPath, string hash, string message, string error)
        {
            try
            {
                JsonUtil.WriteAtomic(Path.Combine(AppPaths.Data, "update-result.json"), new JObject
                {
                    ["state"] = state ?? "unknown",
                    ["target"] = Path.GetFileName(targetPath ?? ""),
                    ["sha256"] = hash ?? "",
                    ["message"] = message ?? "",
                    ["error"] = SecretRedactor.Redact(error ?? ""),
                    ["updatedAt"] = ProviderStore.NowIso()
                });
            }
            catch { }
        }
    }
}

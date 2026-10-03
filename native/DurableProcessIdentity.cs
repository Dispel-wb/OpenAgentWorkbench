using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    // A PID is only a short-lived lookup key on Windows. Persist and verify the
    // process start time and image path before treating a PID as one of ours.
    internal static class DurableProcessIdentity
    {
        private const string IdentitySuffix = ".identity.json";

        public static string StartedAtUtc(Process process)
        {
            return process.StartTime.ToUniversalTime().ToString("o");
        }

        public static long StartedAtUtcTicks(Process process)
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }

        public static string ExecutablePath(Process process)
        {
            string value = null;
            try
            {
                var module = process.MainModule;
                if (module != null) value = module.FileName;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(value) && process.StartInfo != null) value = process.StartInfo.FileName;
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Unable to resolve the process executable path.");
            return Path.GetFullPath(value);
        }

        public static void Write(string pidPath, Process process)
        {
            if (process == null) throw new ArgumentNullException("process");
            var identity = new JObject
            {
                ["schemaVersion"] = 1,
                ["pid"] = process.Id,
                ["executablePath"] = ExecutablePath(process),
                ["startedAtUtc"] = StartedAtUtc(process),
                ["startedAtUtcTicks"] = StartedAtUtcTicks(process),
                ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            JsonUtil.WriteAtomic(pidPath + IdentitySuffix, identity);
            File.WriteAllText(pidPath, process.Id.ToString(), new System.Text.UTF8Encoding(false));
        }

        public static bool TryOpen(string pidPath, out Process process)
        {
            process = null;
            try
            {
                int pid;
                if (string.IsNullOrWhiteSpace(pidPath) || !File.Exists(pidPath) ||
                    !int.TryParse(File.ReadAllText(pidPath).Trim(), out pid) || pid <= 0) return false;
                var candidate = Process.GetProcessById(pid);
                if (candidate.HasExited) { candidate.Dispose(); return false; }

                var identityPath = pidPath + IdentitySuffix;
                if (File.Exists(identityPath))
                {
                    var identity = JsonUtil.Read(identityPath, new JObject()) as JObject ?? new JObject();
                    if ((int?)identity["pid"] != pid || !Matches(candidate,
                        (string)identity["executablePath"], (long?)identity["startedAtUtcTicks"],
                        (string)identity["startedAtUtc"], null))
                    {
                        candidate.Dispose();
                        return false;
                    }
                }
                else
                {
                    // Backward compatibility for jobs created by older builds. The
                    // PID file was written immediately after Process.Start(); a PID
                    // reused hours or days later cannot satisfy this time window.
                    if (!Matches(candidate, null, null, null, pidPath))
                    {
                        candidate.Dispose();
                        return false;
                    }
                }
                process = candidate;
                return true;
            }
            catch
            {
                if (process != null) process.Dispose();
                process = null;
                return false;
            }
        }

        public static bool Matches(Process process, string expectedExecutablePath, long? expectedStartedAtUtcTicks, string expectedStartedAtUtc, string legacyMarkerPath)
        {
            try
            {
                if (process == null || process.HasExited) return false;
                if (!string.IsNullOrWhiteSpace(expectedExecutablePath) &&
                    !string.Equals(ExecutablePath(process), Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;

                if (expectedStartedAtUtcTicks.HasValue)
                    return Math.Abs(StartedAtUtcTicks(process) - expectedStartedAtUtcTicks.Value) <= TimeSpan.FromSeconds(2).Ticks;

                DateTime expectedStart;
                if (DateTime.TryParse(expectedStartedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expectedStart))
                    return Math.Abs((process.StartTime.ToUniversalTime() - expectedStart.ToUniversalTime()).TotalSeconds) <= 2;

                if (string.IsNullOrWhiteSpace(legacyMarkerPath) || !File.Exists(legacyMarkerPath)) return false;
                var marker = File.GetLastWriteTimeUtc(legacyMarkerPath);
                var delta = marker - process.StartTime.ToUniversalTime();
                return delta >= TimeSpan.FromSeconds(-5) && delta <= TimeSpan.FromSeconds(60);
            }
            catch { return false; }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class JsonUtil
    {
        public static JToken Read(string path, JToken fallback)
        {
            try { return JToken.Parse(File.ReadAllText(path)); }
            catch { return fallback.DeepClone(); }
        }

        public static void WriteAtomic(string path, JToken value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, value.ToString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }

    internal static class DurableProcessIdentitySelfTest
    {
        public static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "durable-process-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Process process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 120")
                { UseShellExecute = false, CreateNoWindow = true });
                var pidPath = Path.Combine(root, "child.pid");
                File.WriteAllText(pidPath, process.Id.ToString());
                File.SetLastWriteTimeUtc(pidPath, DateTime.UtcNow.AddHours(-4));
                Process opened;
                if (DurableProcessIdentity.TryOpen(pidPath, out opened)) { opened.Dispose(); return 10; }
                if (process.HasExited) return 11;

                File.SetLastWriteTimeUtc(pidPath, process.StartTime.ToUniversalTime().AddSeconds(1));
                if (!DurableProcessIdentity.TryOpen(pidPath, out opened)) return 12;
                opened.Dispose();

                DurableProcessIdentity.Write(pidPath, process);
                if (!DurableProcessIdentity.TryOpen(pidPath, out opened))
                {
                    Console.Error.WriteLine(File.ReadAllText(pidPath + ".identity.json"));
                    Console.Error.WriteLine("actualPath=" + DurableProcessIdentity.ExecutablePath(process));
                    Console.Error.WriteLine("actualStart=" + DurableProcessIdentity.StartedAtUtc(process));
                    var failedIdentity = (JObject)JsonUtil.Read(pidPath + ".identity.json", new JObject());
                    Console.Error.WriteLine("pidMatch=" + ((int?)failedIdentity["pid"] == process.Id));
                    DateTimeOffset parsedStart;
                    Console.Error.WriteLine("parse=" + DateTimeOffset.TryParse((string)failedIdentity["startedAtUtc"], out parsedStart));
                    Console.Error.WriteLine("processStartRaw=" + process.StartTime.ToString("o") + " kind=" + process.StartTime.Kind);
                    Console.Error.WriteLine("processStartUtc=" + process.StartTime.ToUniversalTime().ToString("o"));
                    Console.Error.WriteLine("parsedUtc=" + parsedStart.UtcDateTime.ToString("o"));
                    Console.Error.WriteLine("delta=" + (process.StartTime.ToUniversalTime() - parsedStart.UtcDateTime).TotalSeconds);
                    Console.Error.WriteLine("pathMatch=" + string.Equals(DurableProcessIdentity.ExecutablePath(process), Path.GetFullPath((string)failedIdentity["executablePath"]), StringComparison.OrdinalIgnoreCase));
                    Console.Error.WriteLine("matches=" + DurableProcessIdentity.Matches(process, (string)failedIdentity["executablePath"], (long?)failedIdentity["startedAtUtcTicks"], (string)failedIdentity["startedAtUtc"], null));
                    return 13;
                }
                opened.Dispose();

                var identityPath = pidPath + ".identity.json";
                var identity = (JObject)JsonUtil.Read(identityPath, new JObject());
                identity["startedAtUtc"] = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");
                identity["startedAtUtcTicks"] = DateTime.UtcNow.AddHours(-2).Ticks;
                JsonUtil.WriteAtomic(identityPath, identity);
                if (DurableProcessIdentity.TryOpen(pidPath, out opened)) { opened.Dispose(); return 14; }
                if (process.HasExited) return 15;

                Console.WriteLine("Durable process identity PASS");
                return 0;
            }
            finally
            {
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
                try { if (process != null) process.Dispose(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}

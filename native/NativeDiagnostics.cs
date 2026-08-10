using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class SecretRedactor
    {
        private static readonly Regex ApiKey = new Regex(@"(?i)\b(sk|key|token)-[a-z0-9_\-]{10,}\b", RegexOptions.Compiled);
        private static readonly Regex Bearer = new Regex(@"(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;""']+", RegexOptions.Compiled);
        private static readonly Regex JsonSecret = new Regex(@"(?i)(""(?:api[_-]?key|token|secret|authorization|authProtected)""\s*:\s*"")[^""]+("")", RegexOptions.Compiled);

        public static string Redact(string value)
        {
            value = value ?? "";
            value = ApiKey.Replace(value, match => match.Groups[1].Value + "-[REDACTED]");
            value = Bearer.Replace(value, "$1[REDACTED]");
            return JsonSecret.Replace(value, "$1[REDACTED]$2");
        }

        public static JToken Sanitize(JToken token)
        {
            if (token == null) return JValue.CreateNull();
            var clone = token.DeepClone();
            var container = clone as JContainer;
            foreach (var property in container == null ? Enumerable.Empty<JProperty>() : container.DescendantsAndSelf().OfType<JProperty>())
            {
                var name = property.Name.ToLowerInvariant();
                if (name.Contains("token") || name.Contains("secret") || name.Contains("key") || name.Contains("authorization") || name.Contains("authprotected")) property.Value = "[REDACTED]";
                else if (property.Value.Type == JTokenType.String) property.Value = Redact((string)property.Value);
            }
            return clone;
        }
    }

    internal static class NativeDiagnostics
    {
        public static JObject Export(AgentEventStore store, string workspace)
        {
            var root = Path.Combine(AppPaths.Data, "diagnostics"); Directory.CreateDirectory(root);
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var staging = Path.Combine(root, ".staging-" + id); var zip = Path.Combine(root, "ClaudeWorkbench-Diagnostics-" + id + ".zip");
            Directory.CreateDirectory(staging);
            try
            {
                var executable = Assembly.GetExecutingAssembly().Location;
                var manifest = new JObject
                {
                    ["createdAt"] = ProviderStore.NowIso(), ["appVersion"] = Program.AppContractVersion,
                    ["os"] = Environment.OSVersion.VersionString, ["is64Bit"] = Environment.Is64BitProcess,
                    ["machine"] = Environment.MachineName, ["user"] = "[REDACTED]", ["workspace"] = workspace ?? "",
                    ["executable"] = executable, ["executableSha256"] = File.Exists(executable) ? Sha256(executable) : "",
                    ["processId"] = Process.GetCurrentProcess().Id, ["eventStore"] = store.Health()
                };
                WriteJson(Path.Combine(staging, "manifest.json"), SecretRedactor.Sanitize(manifest));
                CopyJsonSummary(AppPaths.SettingsFile, Path.Combine(staging, "settings.sanitized.json"));
                CopyJsonSummary(Path.Combine(AppPaths.Data, "runtime-state.json"), Path.Combine(staging, "runtime-state.sanitized.json"));
                CopyRedactedLog(Path.Combine(AppPaths.Data, "native-runtime.log"), Path.Combine(staging, "native-runtime.log"));
                CopyRedactedLog(Path.Combine(AppPaths.Data, "native-crash.log"), Path.Combine(staging, "native-crash.log"));
                WriteJson(Path.Combine(staging, "schedules.sanitized.json"), SecretRedactor.Sanitize(store.ListSchedules(true)));
                ZipFile.CreateFromDirectory(staging, zip, CompressionLevel.Optimal, false, Encoding.UTF8);
                return new JObject { ["path"] = zip, ["size"] = new FileInfo(zip).Length, ["redacted"] = true, ["localOnly"] = true };
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
        }

        private static void CopyJsonSummary(string source, string target)
        {
            JToken value; try { value = File.Exists(source) ? JToken.Parse(File.ReadAllText(source, Encoding.UTF8)) : new JObject { ["missing"] = true }; }
            catch (Exception error) { value = new JObject { ["parseError"] = error.Message }; }
            WriteJson(target, SecretRedactor.Sanitize(value));
        }

        private static void CopyRedactedLog(string source, string target)
        {
            if (!File.Exists(source)) { File.WriteAllText(target, "log missing", new UTF8Encoding(false)); return; }
            var info = new FileInfo(source); var take = (int)Math.Min(info.Length, 2L * 1024 * 1024); byte[] bytes;
            using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            { stream.Seek(-take, SeekOrigin.End); bytes = new byte[take]; var read = stream.Read(bytes, 0, take); if (read != take) Array.Resize(ref bytes, read); }
            File.WriteAllText(target, SecretRedactor.Redact(Encoding.UTF8.GetString(bytes)), new UTF8Encoding(false));
        }

        private static void WriteJson(string path, JToken value) { File.WriteAllText(path, value.ToString(Formatting.Indented), new UTF8Encoding(false)); }
        private static string Sha256(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("X2"))); }
    }
}

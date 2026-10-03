using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class WorkspaceLayout
    {
        public static string UnifiedRoot { get { return AppPaths.Workspace; } }
        public static string CliRoot { get { return AppPaths.CliWorkspaces; } }
        public static string SharedRoot { get { return AppPaths.SharedLibrary; } }
        public static bool IsInside(string root, string candidate)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) || normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        public static string NormalizeSessionWorkspace(string value)
        {
            try
            {
                var candidate = string.IsNullOrWhiteSpace(value) ? UnifiedRoot : Path.GetFullPath(value);
                if (!Directory.Exists(candidate)) return UnifiedRoot;
                if (EditionInfo.IsOpenSource || IsInside(UnifiedRoot, candidate) || IsInside(CliRoot, candidate)) return candidate;
            }
            catch { }
            return UnifiedRoot;
        }
        public static string HarnessRoot(string harness)
        {
            var id = (harness ?? "custom").Trim().ToLowerInvariant();
            foreach (var ch in Path.GetInvalidFileNameChars()) id = id.Replace(ch, '-');
            id = id.Replace(':', '-').Replace('/', '-').Replace('\\', '-');
            if (id.Length == 0) id = "custom";
            return Path.Combine(CliRoot, id);
        }
        public static void Initialize()
        {
            foreach (var path in new[] { CliRoot, SharedRoot, Path.Combine(SharedRoot, "skills"), Path.Combine(SharedRoot, "agents"), Path.Combine(SharedRoot, "mcp"), Path.Combine(SharedRoot, "plugins") }) Directory.CreateDirectory(path);
            foreach (var harness in new[] { "claude", "codex", "pi", "dsh" }) Directory.CreateDirectory(HarnessRoot(harness));
            var manifest = Path.Combine(SharedRoot, "layout.json");
            if (!File.Exists(manifest)) JsonUtil.WriteAtomic(manifest, Snapshot());
        }
        public static JObject Snapshot()
        {
            return new JObject { ["schemaVersion"] = 2, ["unifiedRoot"] = UnifiedRoot, ["cliRoot"] = CliRoot, ["sharedLibrary"] = SharedRoot,
                ["harnessRoots"] = new JObject { ["claude"] = HarnessRoot("claude"), ["codex"] = HarnessRoot("codex"), ["pi"] = HarnessRoot("pi"), ["dsh"] = HarnessRoot("dsh") },
                ["shared"] = new JObject { ["skills"] = Path.Combine(SharedRoot, "skills"), ["agents"] = Path.Combine(SharedRoot, "agents"), ["mcp"] = Path.Combine(SharedRoot, "mcp"), ["mcpFile"] = AppPaths.SharedMcpFile, ["plugins"] = Path.Combine(SharedRoot, "plugins") } };
        }
    }
}

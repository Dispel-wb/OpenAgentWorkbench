using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class TaskSecurityDecision
    {
        public string Behavior = "deny";
        public string Capability = "unknown";
        public string Risk = "high";
        public string Reason = "The tool is not covered by the task permission manifest.";
    }

    internal static class TaskSecurityPolicy
    {
        private static readonly string[] ReadTools = { "read", "glob", "grep", "ls", "notebookread" };
        private static readonly string[] WriteTools = { "write", "edit", "multiedit", "notebookedit" };
        private static readonly string[] NetworkTools = { "webfetch", "websearch" };
        private static readonly Regex DestructiveCommand = new Regex(@"(^|[\s;&|])(rm|del|erase|rmdir|remove-item|rd|format|diskpart|shutdown)(\s|$)|git\s+(clean|reset\s+--hard)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NetworkCommand = new Regex(@"(^|[\s;&|])(curl|wget|invoke-webrequest|irm|iwr)(\s|$)|\b(git\s+(push|pull|fetch|clone)|npm\s+(install|publish)|pip\s+install)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static JObject Create(string jobId, string mode, string workspace, IEnumerable<string> roots, JArray allowedTools, JArray disallowedTools)
        {
            mode = (mode ?? "readonly").ToLowerInvariant();
            var capabilities = new JObject();
            Set(capabilities, "read", "allow");
            Set(capabilities, "write", mode == "edit" || mode == "agent" ? "allow" : mode == "manual" ? "ask" : "deny");
            Set(capabilities, "delete", mode == "manual" || mode == "edit" || mode == "agent" ? "ask" : "deny");
            Set(capabilities, "execute", mode == "agent" ? "allow" : mode == "manual" || mode == "edit" ? "ask" : "deny");
            Set(capabilities, "network", mode == "readonly" || mode == "plan" || mode == "agent" ? "allow" : mode == "manual" || mode == "edit" ? "ask" : "deny");
            Set(capabilities, "unknown", "ask");
            if (mode == "scoped")
            {
                foreach (var property in capabilities.Properties()) property.Value = "deny";
                foreach (var tool in (allowedTools ?? new JArray()).Select(value => ((string)value ?? "").ToLowerInvariant()))
                {
                    if (ReadTools.Contains(tool)) Set(capabilities, "read", "allow");
                    else if (WriteTools.Contains(tool)) Set(capabilities, "write", "allow");
                    else if (NetworkTools.Contains(tool)) Set(capabilities, "network", "allow");
                    else if (tool == "bash") Set(capabilities, "execute", "ask");
                }
            }
            var normalizedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            normalizedRoots.Add(Path.GetFullPath(workspace));
            foreach (var root in roots ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(root)) normalizedRoots.Add(Path.GetFullPath(root));
            return new JObject
            {
                ["schemaVersion"] = 1, ["jobId"] = jobId, ["mode"] = mode, ["workspace"] = Path.GetFullPath(workspace),
                ["roots"] = new JArray(normalizedRoots.OrderBy(value => value)), ["writeRoots"] = new JArray(Path.GetFullPath(workspace)), ["capabilities"] = capabilities,
                ["allowedTools"] = allowedTools == null ? new JArray() : allowedTools.DeepClone(),
                ["disallowedTools"] = disallowedTools == null ? new JArray() : disallowedTools.DeepClone(),
                ["createdAt"] = ProviderStore.NowIso()
            };
        }

        public static TaskSecurityDecision Evaluate(JObject policy, string toolName, JToken input)
        {
            var decision = new TaskSecurityDecision();
            if (policy == null) { decision.Reason = "Task permission manifest is missing."; return decision; }
            var normalizedTool = (toolName ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            var disallowed = policy["disallowedTools"] as JArray ?? new JArray();
            if (disallowed.Any(value => string.Equals((string)value, toolName, StringComparison.OrdinalIgnoreCase)))
            { decision.Reason = "The tool is explicitly denied by this task."; return decision; }

            var command = (string)input?["command"] ?? "";
            if (ReadTools.Contains(normalizedTool)) { decision.Capability = "read"; decision.Risk = "low"; }
            else if (WriteTools.Contains(normalizedTool)) { decision.Capability = "write"; decision.Risk = "medium"; }
            else if (NetworkTools.Contains(normalizedTool)) { decision.Capability = "network"; decision.Risk = "medium"; }
            else if (normalizedTool == "bash")
            {
                decision.Capability = DestructiveCommand.IsMatch(command) ? "delete" : NetworkCommand.IsMatch(command) ? "network" : "execute";
                decision.Risk = decision.Capability == "delete" ? "critical" : "high";
            }
            else { decision.Capability = "unknown"; decision.Risk = "high"; }

            var paths = ExtractPaths(input, (string)policy["workspace"] ?? AppPaths.Workspace).ToArray();
            var rootToken = decision.Capability == "write" || decision.Capability == "delete" ? policy["writeRoots"] as JArray : policy["roots"] as JArray;
            var roots = (rootToken ?? new JArray()).Select(value => (string)value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).ToArray();
            var outside = paths.FirstOrDefault(path => !IsInside(path, roots));
            var mode = (string)policy["mode"] ?? "readonly";
            if (outside != null && mode != "manual" && mode != "agent")
            {
                decision.Behavior = "deny"; decision.Risk = "critical";
                decision.Reason = "Path is outside the roots authorized for this task: " + outside;
                return decision;
            }
            var configured = (string)policy["capabilities"]?[decision.Capability] ?? "deny";
            // Agent/manual tasks may request access beyond their declared roots, but that
            // exception must never inherit the capability's normal automatic allow rule.
            if (outside != null) configured = "ask";
            if (decision.Capability == "delete" && configured == "allow") configured = "ask";
            decision.Behavior = configured == "allow" ? "allow" : configured == "ask" ? "ask" : "deny";
            decision.Reason = outside != null
                ? "This operation targets a path outside the current task roots and requires an explicit one-time decision."
                : decision.Behavior == "allow" ? "Allowed by the task permission manifest."
                : decision.Behavior == "ask" ? "This risk class requires an explicit user decision."
                : "Denied by the task permission manifest.";
            return decision;
        }

        private static IEnumerable<string> ExtractPaths(JToken token, string workspace)
        {
            var names = new HashSet<string>(new[] { "path", "file_path", "filepath", "directory", "dir", "notebook_path" }, StringComparer.OrdinalIgnoreCase);
            foreach (var property in (token as JObject ?? new JObject()).DescendantsAndSelf().OfType<JProperty>())
            {
                if (!names.Contains(property.Name) || property.Value.Type != JTokenType.String) continue;
                var value = ((string)property.Value ?? "").Trim();
                if (value.Length == 0 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) continue;
                string full;
                try { full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workspace, value)); }
                catch { continue; }
                yield return full;
            }
        }

        private static bool IsInside(string path, IEnumerable<string> roots)
        {
            var full = Path.GetFullPath(path);
            foreach (var rootValue in roots)
            {
                var root = Path.GetFullPath(rootValue);
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void Set(JObject target, string name, string value) { target[name] = value; }
    }

    internal static class TaskSecuritySelfTest
    {
        public static int Run(string root)
        {
            try
            {
                Directory.CreateDirectory(root);
                var inside = Path.Combine(root, "folder", "file.txt");
                var outside = Path.Combine(Path.GetDirectoryName(root), "outside.txt");
                var readInput = new JObject { ["file_path"] = inside };
                var outsideInput = new JObject { ["file_path"] = outside };
                var readOnly = TaskSecurityPolicy.Create("r1", "readonly", root, new string[0], new JArray(), new JArray());
                if (TaskSecurityPolicy.Evaluate(readOnly, "Read", readInput).Behavior != "allow") return 41;
                if (TaskSecurityPolicy.Evaluate(readOnly, "Write", readInput).Behavior != "deny") return 42;
                if (TaskSecurityPolicy.Evaluate(readOnly, "Read", outsideInput).Behavior != "deny") return 43;
                var agent = TaskSecurityPolicy.Create("r2", "agent", root, new[] { root }, new JArray(), new JArray());
                if (TaskSecurityPolicy.Evaluate(agent, "Bash", new JObject { ["command"] = "echo safe" }).Behavior != "allow") return 44;
                var destructive = TaskSecurityPolicy.Evaluate(agent, "Bash", new JObject { ["command"] = "Remove-Item -Recurse target" });
                if (destructive.Behavior != "ask" || destructive.Capability != "delete") return 45;
                if (TaskSecurityPolicy.Evaluate(agent, "Write", outsideInput).Behavior != "ask") return 46;
                var manual = TaskSecurityPolicy.Create("r3", "manual", root, new string[0], new JArray(), new JArray());
                if (TaskSecurityPolicy.Evaluate(manual, "Write", outsideInput).Behavior != "ask") return 47;
                var scoped = TaskSecurityPolicy.Create("r4", "scoped", root, new string[0], new JArray("Read"), new JArray("Bash"));
                if (TaskSecurityPolicy.Evaluate(scoped, "Read", readInput).Behavior != "allow") return 48;
                if (TaskSecurityPolicy.Evaluate(scoped, "WebFetch", new JObject()).Behavior != "deny") return 49;
                if (TaskSecurityPolicy.Evaluate(scoped, "Bash", new JObject { ["command"] = "echo blocked" }).Behavior != "deny") return 50;
                return 0;
            }
            catch { return 51; }
        }
    }
}

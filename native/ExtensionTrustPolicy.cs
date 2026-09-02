using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class ExtensionTrustPolicy
    {
        internal const string SkillMetadataName = ".workbench-package.json";

        public static JObject AssessSkill(string directory, string scope)
        {
            return Assess("skill", directory, Path.Combine(directory, SkillMetadataName), scope);
        }

        public static JObject AssessAgent(string file, string scope)
        {
            var metadata = Path.Combine(Path.GetDirectoryName(file), "." + Path.GetFileNameWithoutExtension(file) + ".workbench-package.json");
            return Assess("agent", file, metadata, scope);
        }

        private static JObject Assess(string type, string path, string metadataPath, string scope)
        {
            var metadata = JsonUtil.Read(metadataPath, new JObject()) as JObject ?? new JObject();
            var signature = ((string)metadata["signatureStatus"] ?? "unmanaged").Trim().ToLowerInvariant();
            var expected = ((string)metadata["installedContentSha256"] ?? "").Trim();
            var project = string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase);
            var managed = signature != "unmanaged";
            // Legacy user-owned extensions remain enabled for compatibility. Avoid
            // hashing an entire user Skill library on every metadata index refresh.
            var actual = !project && !managed ? "" : ContentDigest(type, path);
            var revoked = signature == "revoked";
            var integrityTracked = expected.Length > 0;
            var integrityMatches = integrityTracked && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            var recognized = signature == "trusted" || signature == "trusted-local" || signature == "local-development" || signature == "unsigned-development";
            var active = !revoked && ((managed && recognized && integrityMatches) || (!project && !managed));
            var trustState = active
                ? (signature == "trusted" ? "publisher-trusted" : managed ? "local-trusted" : "user-owned-unmanaged")
                : revoked ? "revoked" : managed && integrityTracked && !integrityMatches ? "modified" : "untrusted";
            var reason = active
                ? (signature == "trusted" ? "发布者签名与安装内容校验通过" : managed ? "本机授权与内容指纹匹配" : "用户级旧扩展按兼容策略启用")
                : revoked ? "已被本机用户撤销" : managed && integrityTracked && !integrityMatches ? "内容在授权后发生变化，需要重新确认" : "项目扩展尚未由本机用户确认";
            return new JObject
            {
                ["signatureStatus"] = signature,
                ["trustState"] = trustState,
                ["active"] = active,
                ["reason"] = reason,
                ["contentSha256"] = actual,
                ["integrityTracked"] = integrityTracked,
                ["publisherThumbprint"] = metadata["publisherThumbprint"] ?? "",
                ["packageVersion"] = metadata["version"] ?? ""
            };
        }

        public static JObject WorkspaceSummary(string workspace)
        {
            workspace = Path.GetFullPath(workspace);
            var projectClaude = Path.Combine(workspace, ".claude");
            var inactive = new JArray();
            var skillsRoot = Path.Combine(projectClaude, "skills");
            if (Directory.Exists(skillsRoot))
                foreach (var directory in Directory.EnumerateDirectories(skillsRoot).Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)))
                {
                    if (!File.Exists(Path.Combine(directory, "SKILL.md"))) continue;
                    var trust = AssessSkill(directory, "project");
                    if (!((bool?)trust["active"] ?? false)) inactive.Add(Item("skill", Path.GetFileName(directory), directory, trust));
                }
            var agentsRoot = Path.Combine(projectClaude, "agents");
            if (Directory.Exists(agentsRoot))
                foreach (var file in Directory.EnumerateFiles(agentsRoot, "*.md"))
                {
                    var trust = AssessAgent(file, "project");
                    if (!((bool?)trust["active"] ?? false)) inactive.Add(Item("agent", Path.GetFileNameWithoutExtension(file), file, trust));
                }
            var controls = ControlSurfaces(workspace);
            var containedCount = controls.OfType<JObject>().Count(item => !((bool?)item["active"] ?? false));
            return new JObject
            {
                ["inactive"] = inactive,
                ["inactiveCount"] = inactive.Count,
                ["controls"] = controls,
                ["containedCount"] = containedCount,
                ["activeControlCount"] = controls.Count - containedCount,
                ["implicitLoadingContained"] = true,
                ["blocked"] = inactive.Count > 0
            };
        }

        public static JArray ControlSurfaces(string workspace)
        {
            workspace = Path.GetFullPath(workspace);
            var result = new JArray();
            foreach (var file in EnumerateControlFiles(workspace))
            {
                var relative = Relative(workspace, file);
                var kind = ControlKind(workspace, file);
                var trust = AssessControl(workspace, file, relative, kind);
                var item = new JObject
                {
                    ["type"] = "control", ["name"] = relative, ["relativePath"] = relative,
                    ["path"] = file, ["scope"] = "project", ["kind"] = kind,
                    ["capabilities"] = ControlCapabilities(file, kind)
                };
                foreach (var property in trust.Properties()) item[property.Name] = property.Value.DeepClone();
                result.Add(item);
            }
            return result;
        }

        public static JObject SetControlTrust(string workspace, string relativePath, bool trusted)
        {
            workspace = Path.GetFullPath(workspace);
            relativePath = (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(workspace, relativePath));
            var prefix = workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new FileNotFoundException("项目控制文件不存在", relativePath);
            var known = ControlSurfaces(workspace).OfType<JObject>().FirstOrDefault(item => string.Equals((string)item["path"], full, StringComparison.OrdinalIgnoreCase));
            if (known == null) throw new InvalidOperationException("该文件不是 Claude Code 项目控制面的一部分");
            var ledgerPath = ControlLedgerPath(workspace);
            var ledger = JsonUtil.Read(ledgerPath, new JObject()) as JObject ?? new JObject();
            var entries = ledger["entries"] as JObject;
            if (entries == null) { entries = new JObject(); ledger["entries"] = entries; }
            var key = Relative(workspace, full);
            entries[key] = new JObject
            {
                ["status"] = trusted ? "trusted-local" : "revoked",
                ["sha256"] = Sha256File(full), ["trustedAt"] = ProviderStore.NowIso()
            };
            ledger["workspace"] = workspace; ledger["schemaVersion"] = 1;
            JsonUtil.WriteAtomic(ledgerPath, ledger);
            return AssessControl(workspace, full, key, (string)known["kind"]);
        }

        public static JObject TrustedRuntimeInputs(string workspace)
        {
            workspace = Path.GetFullPath(workspace);
            var instructions = new StringBuilder();
            var instructionSources = new JArray();
            var mcpConfigs = new JArray();
            var agents = new JObject();
            foreach (var item in ControlSurfaces(workspace).OfType<JObject>().Where(item => (bool?)item["active"] ?? false))
            {
                var kind = (string)item["kind"] ?? "";
                var path = (string)item["path"] ?? "";
                if (kind == "claude-md" || kind == "rule")
                {
                    var text = ReadLimitedText(path, 512 * 1024);
                    if (text.Length == 0) continue;
                    if (instructions.Length + text.Length > 2 * 1024 * 1024) break;
                    instructions.AppendLine("<workbench_trusted_project_instruction path=\"" + Relative(workspace, path).Replace("\"", "") + "\">");
                    instructions.AppendLine(text).AppendLine("</workbench_trusted_project_instruction>");
                    instructionSources.Add(new JObject { ["path"] = path, ["sha256"] = item["contentSha256"], ["kind"] = kind });
                }
                else if (kind == "mcp") mcpConfigs.Add(path);
            }
            foreach (var root in new[]
            {
                new { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "agents"), Scope = "user" },
                new { Path = Path.Combine(workspace, ".claude", "agents"), Scope = "project" }
            })
            {
                if (!Directory.Exists(root.Path)) continue;
                foreach (var file in Directory.EnumerateFiles(root.Path, "*.md").Take(100))
                {
                    var trust = AssessAgent(file, root.Scope);
                    if (!((bool?)trust["active"] ?? false)) continue;
                    var definition = AgentDefinition(file);
                    var id = ((string)definition["name"] ?? Path.GetFileNameWithoutExtension(file)).Trim();
                    if (id.Length == 0 || agents[id] != null) continue;
                    definition.Remove("name");
                    agents[id] = definition;
                    if (Encoding.UTF8.GetByteCount(agents.ToString(Newtonsoft.Json.Formatting.None)) > 24000) { agents.Remove(id); break; }
                }
            }
            return new JObject
            {
                ["instructionText"] = instructions.ToString(), ["instructionSources"] = instructionSources,
                ["mcpConfigs"] = mcpConfigs, ["agents"] = agents, ["bareMode"] = true
            };
        }

        public static JObject SetLocalTrust(string type, string path, bool trusted)
        {
            type = (type ?? "").Trim().ToLowerInvariant();
            if (type != "skill" && type != "agent") throw new InvalidOperationException("扩展类型无效");
            var metadataPath = type == "skill"
                ? Path.Combine(path, SkillMetadataName)
                : Path.Combine(Path.GetDirectoryName(path), "." + Path.GetFileNameWithoutExtension(path) + ".workbench-package.json");
            var metadata = JsonUtil.Read(metadataPath, new JObject()) as JObject ?? new JObject();
            metadata["type"] = type;
            metadata["id"] = type == "skill" ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            metadata["signatureStatus"] = trusted ? "trusted-local" : "revoked";
            metadata["trustSource"] = "user-confirmed";
            metadata["trustedAt"] = ProviderStore.NowIso();
            metadata["installedContentSha256"] = ContentDigest(type, path);
            JsonUtil.WriteAtomic(metadataPath, metadata);
            return type == "skill" ? AssessSkill(path, "project") : AssessAgent(path, "project");
        }

        public static JObject ManagedMetadata(JObject metadata, string type, string path, string status)
        {
            var result = metadata == null ? new JObject() : (JObject)metadata.DeepClone();
            result["signatureStatus"] = status;
            result["trustSource"] = status == "trusted" ? "publisher-certificate" : "local-install";
            result["installedContentSha256"] = ContentDigest(type, path);
            return result;
        }

        public static string ContentDigest(string type, string path)
        {
            if (string.Equals(type, "agent", StringComparison.OrdinalIgnoreCase)) return Sha256File(path);
            var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var lines = new List<string>();
            if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(file => !string.Equals(Path.GetFileName(file), SkillMetadataName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = file.Substring(root.Length).Replace('\\', '/');
                    lines.Add(relative + "=" + Sha256File(file));
                }
            return Sha256Text(string.Join("\n", lines));
        }

        private static JObject AssessControl(string workspace, string file, string relative, string kind)
        {
            var ledger = JsonUtil.Read(ControlLedgerPath(workspace), new JObject()) as JObject ?? new JObject();
            var entry = ledger["entries"]?[relative] as JObject ?? new JObject();
            var status = ((string)entry["status"] ?? "unmanaged").Trim().ToLowerInvariant();
            var expected = ((string)entry["sha256"] ?? "").Trim();
            var actual = Sha256File(file);
            var active = status == "trusted-local" && expected.Length > 0 && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            var state = active ? "local-trusted" : status == "revoked" ? "revoked" : expected.Length > 0 && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ? "modified" : "contained";
            var reason = active ? "本机授权与内容指纹匹配；由 bare 模式显式加载"
                : state == "revoked" ? "已撤销；bare 模式不会加载"
                : state == "modified" ? "授权后内容发生变化；bare 模式继续屏蔽"
                : "尚未授权；bare 模式已阻止隐式加载";
            return new JObject
            {
                ["signatureStatus"] = status, ["trustState"] = state, ["active"] = active,
                ["contained"] = !active, ["reason"] = reason, ["contentSha256"] = actual,
                ["kind"] = kind
            };
        }

        private static IEnumerable<string> EnumerateControlFiles(string workspace)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in new[]
            {
                Path.Combine(workspace, ".mcp.json"),
                Path.Combine(workspace, ".claude", "settings.json"),
                Path.Combine(workspace, ".claude", "settings.local.json")
            }) if (File.Exists(file) && found.Add(Path.GetFullPath(file))) yield return Path.GetFullPath(file);

            foreach (var file in EnumerateNamedFiles(workspace, "CLAUDE.md", 100)) if (found.Add(file)) yield return file;
            foreach (var directory in new[] { Path.Combine(workspace, ".claude", "rules"), Path.Combine(workspace, ".claude", "commands") })
            {
                if (!Directory.Exists(directory)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories).Take(100).ToArray(); }
                catch { files = Enumerable.Empty<string>(); }
                foreach (var file in files.Select(Path.GetFullPath)) if (found.Add(file)) yield return file;
            }
        }

        private static IEnumerable<string> EnumerateNamedFiles(string root, string name, int limit)
        {
            var stack = new Stack<string>(); stack.Push(root); var count = 0;
            while (stack.Count > 0 && count < limit)
            {
                var directory = stack.Pop();
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) { yield return Path.GetFullPath(candidate); count++; if (count >= limit) yield break; }
                string[] children;
                try { children = Directory.GetDirectories(directory); } catch { continue; }
                foreach (var child in children)
                {
                    var segment = Path.GetFileName(child);
                    if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase) || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        segment.Equals(".claude-gui-v2", StringComparison.OrdinalIgnoreCase) || segment.StartsWith(".workbench-", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(child);
                }
            }
        }

        private static string ControlKind(string workspace, string file)
        {
            var name = Path.GetFileName(file);
            if (name.Equals(".mcp.json", StringComparison.OrdinalIgnoreCase)) return "mcp";
            if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || name.Equals("settings.local.json", StringComparison.OrdinalIgnoreCase)) return "settings";
            var relative = Relative(workspace, file).Replace('\\', '/');
            if (relative.IndexOf("/.claude/rules/", StringComparison.OrdinalIgnoreCase) >= 0 || relative.StartsWith(".claude/rules/", StringComparison.OrdinalIgnoreCase)) return "rule";
            if (relative.IndexOf("/.claude/commands/", StringComparison.OrdinalIgnoreCase) >= 0 || relative.StartsWith(".claude/commands/", StringComparison.OrdinalIgnoreCase)) return "command";
            return "claude-md";
        }

        private static JArray ControlCapabilities(string file, string kind)
        {
            if (kind == "mcp") return new JArray("local-process", "network", "tools");
            if (kind == "claude-md" || kind == "rule") return new JArray("prompt-instructions");
            if (kind == "command") return new JArray("slash-command");
            var value = JsonUtil.Read(file, new JObject()) as JObject ?? new JObject();
            var capabilities = new JArray("claude-settings");
            if (value["hooks"] != null) capabilities.Add("hook-command-execution");
            if (value["enabledPlugins"] != null) capabilities.Add("plugin-code");
            if (value["mcpServers"] != null) capabilities.Add("mcp-process-network");
            if (value["permissions"] != null) capabilities.Add("permission-policy");
            if (value["sandbox"] != null) capabilities.Add("sandbox-policy");
            return capabilities;
        }

        private static string ControlLedgerPath(string workspace)
        {
            var data = string.IsNullOrWhiteSpace(AppPaths.Data) ? Path.Combine(workspace, ".claude-gui-v2") : AppPaths.Data;
            return Path.Combine(data, "extension-trust", Sha256Text(Path.GetFullPath(workspace).ToLowerInvariant()).Substring(0, 24) + ".json");
        }

        private static string Relative(string root, string file)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length).Replace('\\', '/') : full.Replace('\\', '/');
        }

        private static string ReadLimitedText(string path, int maxBytes)
        {
            try
            {
                var info = new FileInfo(path); if (info.Length <= 0 || info.Length > maxBytes) return "";
                return File.ReadAllText(path, new UTF8Encoding(false, true));
            }
            catch { return ""; }
        }

        private static JObject AgentDefinition(string path)
        {
            var text = ReadLimitedText(path, 256 * 1024); if (text.Length == 0) return new JObject();
            var name = Path.GetFileNameWithoutExtension(path); var description = ""; var tools = ""; var model = ""; var prompt = text;
            using (var reader = new StringReader(text))
            {
                var first = reader.ReadLine();
                if (first != null && first.Trim() == "---")
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim() == "---") break;
                        var separator = line.IndexOf(':'); if (separator <= 0) continue;
                        var key = line.Substring(0, separator).Trim().ToLowerInvariant();
                        var value = line.Substring(separator + 1).Trim().Trim('"', '\'');
                        if (key == "name") name = value; else if (key == "description") description = value; else if (key == "tools") tools = value; else if (key == "model") model = value;
                    }
                    prompt = reader.ReadToEnd().Trim();
                }
            }
            var result = new JObject { ["description"] = description, ["prompt"] = prompt };
            if (tools.Length > 0) result["tools"] = new JArray(tools.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0));
            if (model.Length > 0) result["model"] = model;
            result["name"] = name;
            return result;
        }

        private static JObject Item(string type, string name, string path, JObject trust)
        {
            var item = new JObject { ["type"] = type, ["name"] = name, ["path"] = path };
            foreach (var property in trust.Properties()) item[property.Name] = property.Value.DeepClone();
            return item;
        }

        private static string Sha256File(string path)
        {
            if (!File.Exists(path)) return "";
            using (var sha = SHA256.Create()) using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static string Sha256Text(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
        }
    }
}

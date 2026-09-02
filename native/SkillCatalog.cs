using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class SkillCatalog
    {
        private sealed class CachedIndex
        {
            public DateTimeOffset ExpiresAt;
            public List<JObject> Items = new List<JObject>();
        }

        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, CachedIndex> Cache = new Dictionary<string, CachedIndex>(StringComparer.OrdinalIgnoreCase);

        public static JObject Build(string workspace, string prompt)
        {
            var items = new List<JObject>();
            var roots = new[]
            {
                new { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills"), Scope = "user" },
                new { Path = Path.Combine(workspace, ".claude", "skills"), Scope = "project" }
            };
            foreach (var root in roots)
            {
                items.AddRange(Index(root.Path, root.Scope));
            }
            var matched = new JArray(items.Where(item => ((bool?)item["active"] ?? false) && Matches(prompt, (string)item["name"], (string)item["description"])).Select(item => item.DeepClone()));
            return new JObject { ["schemaVersion"] = 2, ["strategy"] = "trusted-metadata-index-then-Skill-tool", ["items"] = new JArray(items), ["matched"] = matched };
        }

        private static IEnumerable<JObject> Index(string rootPath, string scope)
        {
            if (!Directory.Exists(rootPath)) return Enumerable.Empty<JObject>();
            var key = scope + ":" + Path.GetFullPath(rootPath);
            lock (CacheGate)
            {
                CachedIndex cached;
                if (Cache.TryGetValue(key, out cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
                    return cached.Items.Select(item => (JObject)item.DeepClone()).ToArray();
            }

            var items = new List<JObject>();
            foreach (var directory in Directory.EnumerateDirectories(rootPath).Take(500))
            {
                var file = Path.Combine(directory, "SKILL.md"); if (!File.Exists(file)) continue;
                var metadata = Header(file); var name = (string)metadata["name"];
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(directory);
                var trust = ExtensionTrustPolicy.AssessSkill(directory, scope);
                var item = new JObject
                {
                    ["name"] = name, ["description"] = (string)metadata["description"] ?? "", ["scope"] = scope,
                    ["path"] = file, ["size"] = new FileInfo(file).Length
                };
                foreach (var property in trust.Properties()) item[property.Name] = property.Value.DeepClone();
                items.Add(item);
            }
            lock (CacheGate)
            {
                Cache[key] = new CachedIndex { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(2), Items = items.Select(item => (JObject)item.DeepClone()).ToList() };
            }
            return items;
        }

        public static string RoutingHint(JObject catalog)
        {
            var matched = catalog?["matched"] as JArray ?? new JArray(); if (matched.Count == 0) return "";
            var lines = matched.OfType<JObject>().Select(item => "- " + (string)item["name"] + ": " + (string)item["description"]);
            return "\n\n<workbench_skill_routing>\n以下仅是命中的 Skill 元数据，不含 Skill 正文。需要使用时请通过 Skill 工具按需加载完整 SKILL.md；未命中的 Skill 不得占用上下文。\n" + string.Join("\n", lines) + "\n</workbench_skill_routing>";
        }

        public static void Invalidate()
        {
            lock (CacheGate) Cache.Clear();
        }

        private static JObject Header(string file)
        {
            var result = new JObject();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 2048, false))
            {
                var first = reader.ReadLine(); if (first == null || first.Trim() != "---") return result;
                for (var count = 0; count < 120; count++)
                {
                    var line = reader.ReadLine(); if (line == null || line.Trim() == "---") break;
                    var separator = line.IndexOf(':'); if (separator <= 0) continue;
                    var key = line.Substring(0, separator).Trim(); if (key != "name" && key != "description") continue;
                    result[key] = line.Substring(separator + 1).Trim().Trim('"', '\'');
                }
            }
            return result;
        }

        private static bool Matches(string prompt, string name, string description)
        {
            var text = prompt ?? ""; name = name ?? ""; description = description ?? "";
            if (name.Length > 0 && (text.IndexOf("/" + name, StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return description.Split(new[] { ' ', ',', '，', '。', ';', '；', '/', '\\', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length >= 3).Take(12).Any(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    internal static class SkillCatalogSelfTest
    {
        public static int Run(string root)
        {
            try
            {
                var skill = Path.Combine(root, ".claude", "skills", "review-code"); Directory.CreateDirectory(skill);
                var skillFile = Path.Combine(skill, "SKILL.md");
                File.WriteAllText(skillFile, "---\nname: review-code\ndescription: review source code safely\n---\nFULL_BODY_SENTINEL_MUST_NOT_BE_PRELOADED\n", new UTF8Encoding(false));
                var quarantined = SkillCatalog.Build(root, "please use review-code for this task");
                if ((quarantined["matched"] as JArray ?? new JArray()).Count != 0 || !((bool?)ExtensionTrustPolicy.WorkspaceSummary(root)["blocked"] ?? false)) return 60;
                ExtensionTrustPolicy.SetLocalTrust("skill", skill, true); SkillCatalog.Invalidate();
                var catalog = SkillCatalog.Build(root, "please use review-code for this task");
                if ((catalog["items"] as JArray ?? new JArray()).Count < 1 || (catalog["matched"] as JArray ?? new JArray()).Count != 1) return 61;
                var hint = SkillCatalog.RoutingHint(catalog);
                if (!hint.Contains("review-code") || hint.Contains("FULL_BODY_SENTINEL")) return 62;
                var missed = SkillCatalog.Build(root, "unrelated weather question");
                if ((missed["matched"] as JArray ?? new JArray()).Count != 0 || SkillCatalog.RoutingHint(missed).Length != 0) return 63;
                File.AppendAllText(skillFile, "modified-after-trust\n", new UTF8Encoding(false)); SkillCatalog.Invalidate();
                var modified = SkillCatalog.Build(root, "please use review-code for this task");
                if ((modified["matched"] as JArray ?? new JArray()).Count != 0 || !((bool?)ExtensionTrustPolicy.WorkspaceSummary(root)["blocked"] ?? false)) return 65;
                return 0;
            }
            catch { return 64; }
        }
    }
}

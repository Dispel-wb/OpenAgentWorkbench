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
                if (!Directory.Exists(root.Path)) continue;
                foreach (var directory in Directory.EnumerateDirectories(root.Path).Take(500))
                {
                    var file = Path.Combine(directory, "SKILL.md"); if (!File.Exists(file)) continue;
                    var metadata = Header(file); var name = (string)metadata["name"];
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(directory);
                    items.Add(new JObject
                    {
                        ["name"] = name, ["description"] = (string)metadata["description"] ?? "", ["scope"] = root.Scope,
                        ["path"] = file, ["size"] = new FileInfo(file).Length
                    });
                }
            }
            var matched = new JArray(items.Where(item => Matches(prompt, (string)item["name"], (string)item["description"])).Select(item => item.DeepClone()));
            return new JObject { ["schemaVersion"] = 1, ["strategy"] = "metadata-index-then-Skill-tool", ["items"] = new JArray(items), ["matched"] = matched };
        }

        public static string RoutingHint(JObject catalog)
        {
            var matched = catalog?["matched"] as JArray ?? new JArray(); if (matched.Count == 0) return "";
            var lines = matched.OfType<JObject>().Select(item => "- " + (string)item["name"] + ": " + (string)item["description"]);
            return "\n\n<workbench_skill_routing>\n以下仅是命中的 Skill 元数据，不含 Skill 正文。需要使用时请通过 Skill 工具按需加载完整 SKILL.md；未命中的 Skill 不得占用上下文。\n" + string.Join("\n", lines) + "\n</workbench_skill_routing>";
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
                File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: review-code\ndescription: review source code safely\n---\nFULL_BODY_SENTINEL_MUST_NOT_BE_PRELOADED\n", new UTF8Encoding(false));
                var catalog = SkillCatalog.Build(root, "please use review-code for this task");
                if ((catalog["items"] as JArray ?? new JArray()).Count < 1 || (catalog["matched"] as JArray ?? new JArray()).Count != 1) return 61;
                var hint = SkillCatalog.RoutingHint(catalog);
                if (!hint.Contains("review-code") || hint.Contains("FULL_BODY_SENTINEL")) return 62;
                var missed = SkillCatalog.Build(root, "unrelated weather question");
                if ((missed["matched"] as JArray ?? new JArray()).Count != 0 || SkillCatalog.RoutingHint(missed).Length != 0) return 63;
                return 0;
            }
            catch { return 64; }
        }
    }
}

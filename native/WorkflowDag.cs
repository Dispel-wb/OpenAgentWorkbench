using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class WorkflowDag
    {
        public static JObject Create(JObject input)
        {
            var name = (string)input["name"] ?? "Agent 工作流";
            if (name.Length > 80) throw new InvalidOperationException("工作流名称最多 80 个字符");
            var source = input["nodes"] as JArray;
            if (source == null || source.Count == 0 || source.Count > 32) throw new InvalidOperationException("DAG 需要 1–32 个节点");
            var nodes = new JArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in source)
            {
                var id = (string)item["id"] ?? "";
                var parent = (string)item["parentRunId"] ?? "";
                var prompt = ((string)item["prompt"] ?? "").Trim();
                if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$") || !ids.Add(id)) throw new InvalidOperationException("DAG 节点 ID 无效或重复");
                if (!Regex.IsMatch(parent, "^[a-zA-Z0-9_-]{1,80}$") || prompt.Length == 0 || prompt.Length > 120000) throw new InvalidOperationException("节点需要有效的父 Run 和任务内容");
                var dependencies = item["dependencies"] as JArray ?? new JArray();
                if (dependencies.Count > 31 || dependencies.Any(d => d.Type != JTokenType.String) || dependencies.Values<string>().Distinct().Count() != dependencies.Count)
                    throw new InvalidOperationException("DAG 依赖必须是不重复的节点 ID 数组");
                nodes.Add(new JObject { ["id"] = id, ["parentRunId"] = parent, ["name"] = ((string)item["name"] ?? id).Substring(0, Math.Min(80, ((string)item["name"] ?? id).Length)),
                    ["prompt"] = prompt, ["dependencies"] = dependencies.DeepClone(), ["includeDependencyResults"] = (bool?)item["includeDependencyResults"] ?? false,
                    ["state"] = "pending", ["runId"] = "", ["sessionId"] = Guid.NewGuid().ToString(), ["error"] = "" });
            }
            var visited = new HashSet<string>(); var visiting = new HashSet<string>();
            if (nodes.OfType<JObject>().Sum(n => ((string)n["prompt"]).Length) > 256000)
                throw new InvalidOperationException("一个工作流的任务正文合计不能超过 256,000 个字符");
            var map = nodes.OfType<JObject>().ToDictionary(n => (string)n["id"]);
            foreach (var id in ids) Visit(id, map, visited, visiting);
            return new JObject { ["id"] = Guid.NewGuid().ToString("N"), ["name"] = name,
                ["state"] = "running", ["paused"] = false, ["maxParallel"] = Math.Max(1, Math.Min(4, (int?)input["maxParallel"] ?? 2)),
                ["createdAt"] = ProviderStore.NowIso(), ["nodes"] = nodes };
        }

        private static void Visit(string id, Dictionary<string,JObject> map, HashSet<string> visited, HashSet<string> visiting)
        {
            if (!map.ContainsKey(id)) throw new InvalidOperationException("DAG 依赖节点不存在：" + id);
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidOperationException("DAG 存在循环依赖");
            foreach (var dependency in ((JArray)map[id]["dependencies"]).Values<string>()) Visit(dependency, map, visited, visiting);
            visiting.Remove(id); visited.Add(id);
        }

        public static bool Terminal(string state) { return new[] { "completed", "failed", "cancelled", "blocked", "attention" }.Contains(state); }

        public static void Propagate(JObject workflow)
        {
            var nodes = ((JArray)workflow["nodes"]).OfType<JObject>().ToDictionary(n => (string)n["id"]);
            for (var pass = 0; pass < nodes.Count; pass++)
                foreach (var node in nodes.Values.Where(n => (string)n["state"] == "pending"))
                    if (((JArray)node["dependencies"]).Values<string>().Any(id => Terminal((string)nodes[id]["state"]) && (string)nodes[id]["state"] != "completed"))
                    { node["state"] = "blocked"; node["error"] = "前置节点未成功；没有自动重试或执行后续副作用。"; }
            if (nodes.Values.All(n => Terminal((string)n["state"])))
                workflow["state"] = nodes.Values.All(n => (string)n["state"] == "completed") ? "completed" : "failed";
        }

        public static IEnumerable<JObject> Ready(JObject workflow)
        {
            if ((bool?)workflow["paused"] == true || (string)workflow["state"] != "running") return new JObject[0];
            var nodes = ((JArray)workflow["nodes"]).OfType<JObject>().ToDictionary(n => (string)n["id"]);
            return nodes.Values.Where(n => (string)n["state"] == "pending" &&
                ((JArray)n["dependencies"]).Values<string>().All(id => (string)nodes[id]["state"] == "completed")).ToArray();
        }
    }
}

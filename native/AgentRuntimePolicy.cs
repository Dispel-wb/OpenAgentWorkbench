using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    /// <summary>
    /// Durable parent boundary for Claude Code's in-process sub-agents. Claude
    /// remains responsible for executing the child calls; this manifest is the
    /// Workbench-owned contract they must not exceed.
    /// </summary>
    internal static class AgentRuntimePolicy
    {
        private const int MaxChildren = 4;
        private const int MaxAgentDefinitions = 32;
        private const int MaxPromptBytes = 256 * 1024;

        public static JObject Build(string runId, string taskId, string parentRunId, int depth, string sourceWorkspace,
            string workerWorkspace, string permissionMode, JObject security, JObject toolRuntime,
            JObject trustedAgents, JObject mcpRuntime, JArray allowedTools, JArray disallowedTools)
        {
            var parent = new JObject
            {
                ["runId"] = runId ?? "", ["taskId"] = taskId ?? "", ["parentRunId"] = parentRunId ?? "",
                ["sourceWorkspace"] = Path.GetFullPath(sourceWorkspace), ["workerWorkspace"] = Path.GetFullPath(workerWorkspace),
                ["permissionMode"] = (permissionMode ?? "readonly").ToLowerInvariant(),
                ["roots"] = security?["roots"]?.DeepClone() ?? new JArray(Path.GetFullPath(workerWorkspace)),
                ["writeRoots"] = security?["writeRoots"]?.DeepClone() ?? new JArray(Path.GetFullPath(workerWorkspace)),
                ["allowedTools"] = (allowedTools ?? new JArray()).DeepClone(),
                ["disallowedTools"] = (disallowedTools ?? new JArray()).DeepClone()
            };
            var definitions = new JObject();
            foreach (var property in (trustedAgents ?? new JObject()).Properties().Take(MaxAgentDefinitions))
            {
                var definition = property.Value as JObject;
                if (definition == null) continue;
                var safe = SanitizeDefinition(property.Name, definition, allowedTools);
                if (safe != null) definitions[property.Name] = safe;
            }
            var runtime = new JObject
            {
                ["schemaVersion"] = 1,
                ["mode"] = "claude-code-child-boundary",
                ["depth"] = Math.Max(0, depth),
                ["parent"] = parent,
                ["agents"] = definitions,
                ["maxChildren"] = MaxChildren,
                ["maxDepth"] = 2,
                ["inheritance"] = new JObject
                {
                    ["workspace"] = "restrict_only",
                    ["permissions"] = "restrict_only",
                    ["tools"] = "restrict_only",
                    ["mcp"] = "explicit_only",
                    ["toolRuntime"] = "parent_policy",
                    ["provider"] = "parent_route"
                },
                ["failurePropagation"] = "child_failure_marks_parent_warning",
                ["mcpServerCount"] = (long?)mcpRuntime?["serverCount"] ?? 0L,
                ["toolRuntimePolicy"] = (toolRuntime ?? ToolRuntimePolicy.DefaultManifest()).DeepClone(),
                ["countedInPrompt"] = false,
                ["createdAt"] = ProviderStore.NowIso()
            };
            return runtime;
        }

        public static void Validate(JObject runtime, string sourceWorkspace, string workerWorkspace)
        {
            if (runtime == null || (int?)runtime["schemaVersion"] != 1) throw new InvalidOperationException("Agent Runtime boundary is missing or unsupported.");
            var parent = runtime["parent"] as JObject;
            if (parent == null || !SamePath((string)parent["sourceWorkspace"], sourceWorkspace) || !SamePath((string)parent["workerWorkspace"], workerWorkspace))
                throw new InvalidOperationException("Agent Runtime workspace boundary is invalid.");
            if ((int?)runtime["maxChildren"] > MaxChildren || (int?)runtime["maxDepth"] > 2) throw new InvalidOperationException("Agent Runtime limits exceed the host policy.");
            if (runtime["inheritance"]?["mcp"]?.ToString() != "explicit_only") throw new InvalidOperationException("Agent Runtime MCP inheritance must be explicit.");
            foreach (var property in (runtime["agents"] as JObject ?? new JObject()).Properties())
            {
                var definition = property.Value as JObject;
                if (definition == null || EncodingBytes((string)definition["prompt"] ?? "") > MaxPromptBytes) throw new InvalidOperationException("Agent definition is too large.");
            }
        }

        private static JObject SanitizeDefinition(string name, JObject definition, JArray parentAllowedTools)
        {
            var prompt = (string)definition["prompt"] ?? "";
            if (EncodingBytes(prompt) > MaxPromptBytes) prompt = Clip(prompt, MaxPromptBytes);
            var result = new JObject { ["name"] = name ?? "agent", ["prompt"] = prompt };
            var requested = definition["tools"] as JArray;
            if (requested != null)
            {
                var allowed = new HashSet<string>((parentAllowedTools ?? new JArray()).Values<string>(), StringComparer.OrdinalIgnoreCase);
                result["tools"] = new JArray(requested.Values<string>().Where(tool => allowed.Count == 0 || allowed.Contains(tool)).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace((string)definition["description"])) result["description"] = Clip((string)definition["description"], 4096);
            if (!string.IsNullOrWhiteSpace((string)definition["model"])) result["model"] = (string)definition["model"];
            return result;
        }

        private static bool SamePath(string left, string right)
        {
            try { return string.Equals(Path.GetFullPath(left ?? ""), Path.GetFullPath(right ?? ""), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        private static int EncodingBytes(string value) { return System.Text.Encoding.UTF8.GetByteCount(value ?? ""); }
        private static string Clip(string value, int maxBytes)
        {
            value = value ?? ""; if (EncodingBytes(value) <= maxBytes) return value;
            var output = value; while (output.Length > 0 && EncodingBytes(output) > maxBytes) output = output.Substring(0, output.Length - 1);
            return output;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    /// <summary>
    /// Validates the explicit MCP inputs passed to Claude Code and creates a
    /// redacted runtime manifest. MCP remains executed by Claude Code, but the
    /// Workbench now has a durable, inspectable boundary before Worker start.
    /// </summary>
    internal static class McpRuntimePolicy
    {
        private const long MaxConfigBytes = 1024 * 1024;
        private const int MaxServersPerConfig = 100;

        public static JObject ValidateTrustedConfigs(JArray paths, string workspace)
        {
            return Build(paths, new[] { workspace }, false);
        }

        public static JObject BuildRuntimeManifest(JArray paths, string workspace, string runDir)
        {
            return Build(paths, new[] { workspace, runDir }, true);
        }

        private static JObject Build(JArray paths, IEnumerable<string> roots, bool allowRunConfig)
        {
            var normalizedRoots = (roots ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var configs = new JArray();
            var servers = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in paths ?? new JArray())
            {
                var raw = ((string)token ?? "").Trim();
                if (raw.Length == 0) continue;
                string path;
                try { path = Path.GetFullPath(raw); }
                catch { throw new InvalidOperationException("MCP 配置路径无效"); }
                if (!seen.Add(path)) continue;
                if (!File.Exists(path)) throw new InvalidOperationException("MCP 配置文件不存在");
                if (!IsAllowed(path, normalizedRoots)) throw new InvalidOperationException("MCP 配置超出当前任务的授权根目录");
                var info = new FileInfo(path);
                if (info.Length > MaxConfigBytes) throw new InvalidOperationException("MCP 配置文件超过 1 MB 限制");
                JObject document;
                try { document = JToken.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8)) as JObject; }
                catch { throw new InvalidOperationException("MCP 配置不是有效 JSON"); }
                if (document == null) throw new InvalidOperationException("MCP 配置根节点必须是 JSON 对象");
                var mcpServers = document["mcpServers"] as JObject;
                if (mcpServers == null) throw new InvalidOperationException("MCP 配置缺少 mcpServers 对象");
                if (mcpServers.Count > MaxServersPerConfig) throw new InvalidOperationException("单个 MCP 配置的服务器数量超过限制");
                var configSummary = new JObject
                {
                    ["path"] = path,
                    ["serverCount"] = mcpServers.Count,
                    ["servers"] = new JArray()
                };
                foreach (var property in mcpServers.Properties())
                {
                    var name = property.Name.Trim();
                    if (name.Length == 0 || name.Length > 160) throw new InvalidOperationException("MCP 服务器名称无效");
                    var server = property.Value as JObject;
                    if (server == null) throw new InvalidOperationException("MCP 服务器配置必须是对象");
                    var command = ((string)server["command"] ?? "").Trim();
                    var url = ((string)server["url"] ?? "").Trim();
                    if (command.Length == 0 && url.Length == 0) throw new InvalidOperationException("MCP 服务器缺少 command 或 url");
                    if (command.Length > 2048 || url.Length > 4096) throw new InvalidOperationException("MCP 服务器入口长度超过限制");
                    if (server["env"] != null && (!(server["env"] is JObject) || ((JObject)server["env"]).Properties().Any(p => p.Value.Type != JTokenType.String)))
                        throw new InvalidOperationException("MCP 服务器 " + name + " 的 env 必须是字符串字典；数字、布尔值也必须用引号包裹");
                    if (server["args"] != null && (!(server["args"] is JArray) || ((JArray)server["args"]).Any(v => v.Type != JTokenType.String)))
                        throw new InvalidOperationException("MCP 服务器 " + name + " 的 args 必须是字符串数组");
                    var transport = command.Length > 0 ? "stdio" : InferHttpTransport(url);
                    var capabilities = new JArray(command.Length > 0 ? "local-process" : "network");
                    if (server["args"] is JArray args && args.Count > 0) capabilities.Add("arguments");
                    if (server["env"] is JObject env && env.Count > 0) capabilities.Add("environment");
                    var summary = new JObject
                    {
                        ["name"] = name,
                        ["transport"] = transport,
                        ["capabilities"] = capabilities,
                        ["hasCommand"] = command.Length > 0,
                        ["hasUrl"] = url.Length > 0,
                        ["argumentCount"] = server["args"] is JArray args2 ? args2.Count : 0,
                        ["environmentKeyCount"] = server["env"] is JObject env2 ? env2.Count : 0
                    };
                    (configSummary["servers"] as JArray).Add(summary.DeepClone());
                    servers.Add(summary);
                }
                configs.Add(configSummary);
            }
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["configCount"] = configs.Count,
                ["serverCount"] = servers.Count,
                ["configs"] = configs,
                ["servers"] = servers,
                ["redacted"] = true,
                ["allowRunConfig"] = allowRunConfig
            };
        }

        private static bool IsAllowed(string path, IEnumerable<string> roots)
        {
            var full = Path.GetFullPath(path);
            foreach (var root in roots ?? Enumerable.Empty<string>())
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string InferHttpTransport(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != "http" && parsed.Scheme != "https"))
                throw new InvalidOperationException("MCP url 必须是 http 或 https 地址");
            return parsed.Scheme == "https" ? "https" : "http";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class AgentWorkerLaunch
    {
        internal string Harness;
        internal string Executable;
        internal List<string> Arguments = new List<string>();
    }

    internal static class AgentWorkerSdk
    {
        internal const string Protocol = "claude-stream-json-v1";
        internal const string ContractId = "workbench-agent-worker/1";

        internal static JObject Capabilities(string harness)
        {
            var claude = harness == "claude"; var codex = harness == "codex"; var dsh = harness == "dsh"; var pi = harness == "pi";
            return new JObject {
                ["contractId"] = ContractId, ["eventProtocol"] = Protocol, ["requiresClaudeCli"] = claude,
                ["transport"] = pi ? "pi-rpc-jsonl" : dsh ? "json-rpc-stdio" : codex ? "codex-exec-jsonl" : "jsonl-stdio",
                ["resume"] = claude || codex || pi ? "core-persisted-session" : dsh ? "live-process-only" : "worker-defined",
                ["liveSteering"] = claude, ["workbenchInteractiveApproval"] = claude,
                ["workbenchToolFilter"] = claude, ["processTreeCancellation"] = true,
                ["customWorkerPolicyVerified"] = false
            };
        }

        internal static AgentWorkerLaunch Resolve(JObject request, string runDirectory)
        {
            var requested = SelectedHarness(request);
            if (requested == "claude")
            {
                var executable = NativeWorkerHandle.FindClaudeExecutable();
                if (executable.Length == 0) throw new FileNotFoundException("未找到 Claude Code。请安装或选择 claude.exe，也可以把 Agent Worker 切换为 Codex。");
                return new AgentWorkerLaunch { Harness = "claude", Executable = executable, Arguments = NativeWorkerHandle.BuildClaudeArguments(request) };
            }
            if (requested == "codex")
            {
                if (new[] { "manual", "scoped" }.Contains((string)request["permissionMode"]))
                    throw new InvalidOperationException("Codex exec 核心尚未接入工作台的交互审批和工具白名单，请选择只读或工作区权限；不会忽略这些限制执行。");
                var executable = FindCodexExecutable();
                if (executable.Length == 0) throw new FileNotFoundException("未找到 Codex CLI。请安装 codex.exe，或切换到其他 Agent Worker。");
                var configPath = Path.Combine(runDirectory, "agent-worker-bridge.json");
                JsonUtil.WriteAtomic(configPath, new JObject
                {
                    ["schemaVersion"] = 1, ["harness"] = "codex", ["protocol"] = Protocol,
                    ["executable"] = executable, ["workspace"] = request["workspace"], ["model"] = request["workerModel"],
                    ["permissionMode"] = request["permissionMode"], ["sessionId"] = request["sessionId"],
                    ["addDirs"] = request["addDirs"]?.DeepClone() ?? new JArray(),
                    ["statePath"] = SessionStatePath("codex", (string)request["sessionId"] ?? "")
                });
                return new AgentWorkerLaunch
                {
                    Harness = "codex", Executable = System.Windows.Forms.Application.ExecutablePath,
                    Arguments = new List<string> { "--agent-worker-bridge", configPath }
                };
            }

            if (requested == "dsh")
            {
                var entry = FindDshEntry(); var node = FindNodeExecutable();
                var runtime = DshDiagnostics(entry, node, false);
                if (!((bool)runtime["available"])) throw new InvalidOperationException((string)runtime["probeError"] ?? "未找到 DSHarness CLI 核心或 Node.js。");
                var permission = ((string)request["permissionMode"] ?? "readonly").ToLowerInvariant();
                if (permission == "manual" || permission == "scoped") throw new InvalidOperationException("DSHarness SDK 尚未提供交互审批或工具白名单接口，请使用只读、工作区写入或完整权限模式。");
                var configPath = Path.Combine(runDirectory, "agent-worker-bridge.json");
                var patchPath = Path.Combine(runDirectory, "dsh-workbench.patch.json");
                JsonUtil.WriteAtomic(patchPath, new JArray(
                    new JObject { ["id"] = "session-telemetry-otel", ["disabled"] = true },
                    new JObject { ["id"] = "session-log-deepseek", ["disabled"] = true },
                    new JObject { ["id"] = "plugin-package-inventory-deepseek", ["disabled"] = true }));
                JsonUtil.WriteAtomic(configPath, new JObject
                {
                    ["schemaVersion"] = 1, ["harness"] = "dsh", ["protocol"] = Protocol,
                    ["executable"] = node, ["entry"] = entry, ["profile"] = "sdk", ["patchPath"] = patchPath,
                    ["workspace"] = request["workspace"], ["model"] = request["model"],
                    ["permissionMode"] = permission, ["sessionId"] = request["sessionId"],
                    ["home"] = Path.Combine(AppPaths.Data, "worker-homes", "dsh"), ["maxTurns"] = request["maxTurns"]
                });
                return new AgentWorkerLaunch { Harness = "dsh", Executable = System.Windows.Forms.Application.ExecutablePath,
                    Arguments = new List<string> { "--agent-worker-bridge", configPath } };
            }

            if (requested == "pi")
            {
                var error = CompatibilityError(request);
                if (error.Length > 0) throw new InvalidOperationException(error);
                var runtime = PiDiagnostics(true);
                if (!((bool)runtime["available"])) throw new InvalidOperationException((string)runtime["probeError"]);
                var provider = request["provider"] as JObject ?? new JObject();
                var protocol = (string)provider["protocol"];
                if (protocol != "openai" && protocol != "anthropic") throw new InvalidOperationException("Pi 需要 OpenAI Chat Completions 或 Anthropic Messages 文字接口。");
                var authStyle = (string)provider["authStyle"] ?? "auto";
                if ((protocol == "openai" && authStyle == "x-api-key") || (protocol == "anthropic" && authStyle == "bearer"))
                    throw new InvalidOperationException("Pi 当前使用协议原生鉴权：OpenAI 为 Bearer，Anthropic 为 x-api-key；此自定义鉴权组合暂不支持，请使用其他核心。");
                var model = ((string)request["model"] ?? "").Trim();
                if (model.Length == 0) throw new InvalidOperationException("请为 Pi 选择文字模型。");
                var home = Path.Combine(runDirectory, "pi-home");
                Directory.CreateDirectory(home);
                JsonUtil.WriteAtomic(Path.Combine(home, "models.json"), new JObject { ["providers"] = new JObject { ["workbench"] = new JObject {
                    ["baseUrl"] = (string)provider["sourceBaseUrl"] ?? (string)provider["baseUrl"],
                    ["api"] = protocol == "anthropic" ? "anthropic-messages" : "openai-completions", ["apiKey"] = "$WORKBENCH_PI_API_KEY",
                    ["models"] = new JArray(new JObject { ["id"] = model, ["input"] = new JArray("text"), ["reasoning"] = false }) } } });
                JsonUtil.WriteAtomic(Path.Combine(home, "settings.json"), new JObject { ["packages"] = new JArray(), ["retry"] = new JObject { ["enabled"] = false }, ["compaction"] = new JObject { ["enabled"] = false } });
                var configPath = Path.Combine(runDirectory, "agent-worker-bridge.json");
                JsonUtil.WriteAtomic(configPath, new JObject {
                    ["harness"] = "pi", ["executable"] = runtime["node"], ["entry"] = runtime["path"], ["home"] = home,
                    ["workspace"] = request["workspace"], ["model"] = model, ["permissionMode"] = request["permissionMode"],
                    ["sessionId"] = request["sessionId"], ["statePath"] = SessionStatePath("pi", (string)request["sessionId"] ?? ""),
                    ["maxTurns"] = request["maxTurns"], ["trustedInstructionPath"] = request["trustedInstructionPath"] });
                return new AgentWorkerLaunch { Harness = "pi", Executable = System.Windows.Forms.Application.ExecutablePath,
                    Arguments = new List<string> { "--agent-worker-bridge", configPath } };
            }

            var customId = requested.StartsWith("custom:", StringComparison.Ordinal) ? requested.Substring(7) : requested;
            var definition = CustomWorkers().OfType<JObject>().FirstOrDefault(item => string.Equals((string)item["id"], customId, StringComparison.OrdinalIgnoreCase));
            if (definition == null)
                throw new InvalidOperationException("Agent Worker 不可用。可选值为 auto、claude、codex、dsh、pi 或 custom:<id>。");
            if (!string.Equals((string)definition["protocol"], Protocol, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自定义 Agent Worker 必须实现 " + Protocol + " 协议。");
            var customExecutable = FullExistingExecutable((string)definition["executable"]);
            var arguments = new List<string>();
            foreach (var value in definition["arguments"] as JArray ?? new JArray())
                arguments.Add(ReplaceTemplate((string)value ?? "", request));
            return new AgentWorkerLaunch { Harness = "custom:" + customId, Executable = customExecutable, Arguments = arguments };
        }

        internal static string SelectedHarness(JObject request = null)
        {
            var requested = ((string)request?["workerHarness"] ?? ConfiguredHarness()).Trim().ToLowerInvariant();
            if (requested.Length == 0 || requested == "auto")
                return NativeWorkerHandle.FindClaudeExecutable().Length > 0 ? "claude" : FindCodexExecutable().Length > 0 ? "codex" : "unavailable";
            return requested;
        }

        internal static string CompatibilityError(JObject request)
        {
            var harness = SelectedHarness(request);
            if (harness == "pi")
            {
                var piMode = ((string)request["permissionMode"] ?? "agent").Trim().ToLowerInvariant();
                if (!new[] { "readonly", "plan", "full" }.Contains(piMode) || (request["disallowedTools"] as JArray)?.Count > 0)
                    return "Pi 暂仅支持只读/规划（禁用全部工具）和完整权限（无工作区沙箱）；不支持工作区限定写入、交互审批及工具黑白名单。不会自动提升权限。";
                if ((request["attachments"] as JArray)?.Count > 0)
                    return "Pi 适配器暂仅支持文字输入，请移除附件，或使用其他核心。";
                return "";
            }
            if (harness != "codex" && harness != "dsh") return "";
            var mode = ((string)request["permissionMode"] ?? "agent").Trim().ToLowerInvariant();
            if (mode == "manual" || mode == "scoped" || (request["disallowedTools"] as JArray)?.Count > 0)
                return harness + " 命令行核心尚未接入工作台交互审批与工具黑白名单；这些限制不会被忽略。请选择只读或工作区权限，或使用 Claude 核心。";
            return "";
        }

        internal static JObject Diagnostics(bool probe, JObject request = null)
        {
            var claude = NativeWorkerHandle.ClaudeRuntimeDiagnostics(probe);
            var codexPath = FindCodexExecutable();
            var codex = ProbeExecutable("codex", codexPath, probe);
            var dshEntry = FindDshEntry(); var node = FindNodeExecutable();
            var dsh = DshDiagnostics(dshEntry, node, probe);
            var pi = PiDiagnostics(probe);
            var configured = ConfiguredHarness();
            var selected = SelectedHarness(request);
            var available = selected == "claude" ? ((bool?)claude["available"] ?? false)
                : selected == "codex" ? ((bool?)codex["available"] ?? false)
                : selected == "dsh" ? ((bool?)dsh["available"] ?? false)
                : selected == "pi" ? ((bool?)pi["available"] ?? false)
                : selected.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)
                    ? CustomWorkers().OfType<JObject>().Any(item => string.Equals("custom:" + (string)item["id"], selected, StringComparison.OrdinalIgnoreCase) && IsExistingExecutable((string)item["executable"]))
                    : false;
            return new JObject
            {
                ["schemaVersion"] = 1, ["protocol"] = Protocol, ["configured"] = configured, ["selected"] = selected,
                ["capabilities"] = Capabilities(selected),
                ["available"] = available, ["claude"] = claude, ["codex"] = codex, ["dsh"] = dsh, ["pi"] = pi,
                ["custom"] = new JArray(CustomWorkers().OfType<JObject>().Select(item => new JObject
                {
                    ["id"] = item["id"], ["name"] = item["name"] ?? item["id"], ["protocol"] = item["protocol"],
                    ["available"] = IsExistingExecutable((string)item["executable"])
                }))
            };
        }

        internal static string FindCodexExecutable()
        {
            var settings = Settings();
            var candidates = new List<string>
            {
                (string)settings["codexExecutable"], Environment.GetEnvironmentVariable("CLAUDE_GUI_CODEX_EXE"),
                Path.Combine(AppContext.BaseDirectory, "codex.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin", "codex.exe")
            };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim().Trim('"'), "codex.exe"));
            // Desktop installations keep the CLI in a versioned directory, not always on the Host's PATH.
            var desktopBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            try { if (Directory.Exists(desktopBin)) candidates.AddRange(Directory.GetDirectories(desktopBin).OrderByDescending(Directory.GetLastWriteTimeUtc).Select(directory => Path.Combine(directory, "codex.exe"))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return candidates.Select(SafeFullPath).FirstOrDefault(IsExistingExecutable) ?? "";
        }

        private static JObject DshDiagnostics(string entry, string node, bool probe)
        {
            var result = new JObject { ["available"] = false, ["path"] = entry, ["node"] = node, ["profile"] = "sdk", ["testedVersion"] = "0.1.2-alpha.5" };
            if (entry.Length == 0 || node.Length == 0) { result["probeError"] = "未找到 DSHarness CLI 或 Node.js，请安装配套运行时或设置入口路径。"; return result; }
            try
            {
                var manifest = JsonUtil.Read(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry), "..", "package.json")), new JObject()) as JObject;
                result["version"] = manifest?["version"];
                if ((string)manifest?["name"] != "@deepseek-ai/dsh" || manifest?["dependencies"]?["@deepseek-ai/dsh-sdk-app"] == null)
                { result["probeError"] = "此入口不是带 SDK profile 的 DSHarness CLI；已验证版本为 0.1.2-alpha.5，旧版 latest 不适用。"; return result; }
                result["available"] = true;
                result["versionVerified"] = (string)manifest["version"] == "0.1.2-alpha.5";
                if (probe)
                {
                    var nodeProbe = ProbeExecutable("node", node, true);
                    Version version;
                    var ok = ((bool?)nodeProbe["probeOk"] ?? false) && Version.TryParse(((string)nodeProbe["version"] ?? "").TrimStart('v'), out version) && version >= new Version(22, 19, 0);
                    result["nodeVersion"] = nodeProbe["version"]; result["probeOk"] = ok; result["available"] = ok;
                    if (!ok) result["probeError"] = "DSHarness 需要可运行的 Node.js 22.19 或更新版本。";
                }
            }
            catch (Exception error) { result["available"] = false; result["probeError"] = Limit(error.Message, 1000); }
            return result;
        }

        internal static void ApplyProviderEnvironment(ProcessStartInfo startInfo, JObject request, string harness)
        {
            if (harness != "dsh" && harness != "pi" && !harness.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) return;
            var provider = request["provider"] as JObject;
            if (provider == null || string.IsNullOrWhiteSpace((string)provider["tokenEncrypted"])) return;
            var baseUrl = ((string)provider["sourceBaseUrl"] ?? (string)provider["baseUrl"] ?? "").Trim().TrimEnd('/');
            if (harness == "pi")
            {
                startInfo.EnvironmentVariables["WORKBENCH_PI_API_KEY"] = SecretStore.Unprotect((string)provider["tokenEncrypted"]);
                return;
            }
            if (harness == "dsh")
            {
                if ((string)provider["protocol"] == "anthropic")
                {
                    if (baseUrl == "https://api.deepseek.com/anthropic") baseUrl = "https://api.deepseek.com";
                    else throw new InvalidOperationException("DSHarness 当前使用 DeepSeek Chat Completions 适配器，不能把 Anthropic Messages 地址当作相同协议。请配置兼容文字接口。");
                }
                startInfo.EnvironmentVariables["DEEPSEEK_API_KEY"] = SecretStore.Unprotect((string)provider["tokenEncrypted"]);
                if (baseUrl.Length > 0) startInfo.EnvironmentVariables["DEEPSEEK_BASE_URL"] = baseUrl;
                return;
            }
            startInfo.EnvironmentVariables["OPENAI_API_KEY"] = SecretStore.Unprotect((string)provider["tokenEncrypted"]);
            if (baseUrl.Length > 0) startInfo.EnvironmentVariables["OPENAI_BASE_URL"] = baseUrl;
        }

        internal static string FindDshEntry()
        {
            var candidates = new List<string> { (string)Settings()["dshEntry"], Environment.GetEnvironmentVariable("CLAUDE_GUI_DSH_ENTRY"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
                Path.Combine(AppPaths.ClaudeRoot, "runtimes", "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js") };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim().Trim('"'), "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));
            return candidates.Select(SafeFullPath).FirstOrDefault(IsExistingExecutable) ?? "";
        }

        internal static string FindPiEntry()
        {
            var relative = Path.Combine("runtimes", "pi", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "bundle", "cli.js");
            var candidates = new[] { (string)Settings()["piEntry"], Environment.GetEnvironmentVariable("CLAUDE_GUI_PI_ENTRY"),
                Path.Combine(AppContext.BaseDirectory, relative), Path.Combine(AppPaths.ClaudeRoot, relative) };
            return candidates.Select(SafeFullPath).FirstOrDefault(IsExistingExecutable) ?? "";
        }

        private static JObject PiDiagnostics(bool probe)
        {
            var entry = FindPiEntry(); var node = FindNodeExecutable();
            var result = new JObject { ["available"] = false, ["path"] = entry, ["node"] = node, ["testedVersion"] = "0.85.1" };
            try
            {
                if (entry.Length == 0 || node.Length == 0) throw new InvalidOperationException("未找到 Pi 运行时或 Node.js，请设置 Pi CLI 入口路径。");
                var manifest = JsonUtil.Read(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry), "..", "..", "package.json")), new JObject()) as JObject;
                result["version"] = manifest?["version"];
                if ((string)manifest?["name"] != "@earendil-works/pi-coding-agent" || (string)manifest?["version"] != "0.85.1")
                    throw new InvalidOperationException("Pi 适配器要求已验证的 @earendil-works/pi-coding-agent 0.85.1（agent_settled 协议）。");
                result["available"] = true;
                if (probe)
                {
                    var nodeProbe = ProbeExecutable("node", node, true); Version version;
                    var ok = ((bool?)nodeProbe["probeOk"] ?? false) && Version.TryParse(((string)nodeProbe["version"] ?? "").TrimStart('v'), out version) && version >= new Version(22, 19, 0);
                    result["nodeVersion"] = nodeProbe["version"]; result["probeOk"] = ok;
                    if (!ok) throw new InvalidOperationException("Pi 需要 Node.js 22.19 或更新版本。");
                }
            }
            catch (Exception error) { result["available"] = false; result["probeError"] = Limit(error.Message, 1000); }
            return result;
        }

        internal static string FindNodeExecutable()
        {
            var candidates = new List<string> { (string)Settings()["nodeExecutable"], Environment.GetEnvironmentVariable("CLAUDE_GUI_NODE_EXE"), Path.Combine(AppContext.BaseDirectory, "node.exe") };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(directory)) candidates.Add(Path.Combine(directory.Trim().Trim('"'), "node.exe"));
            return candidates.Select(SafeFullPath).FirstOrDefault(IsExistingExecutable) ?? "";
        }

        private static string SessionStatePath(string harness, string sessionId)
        {
            using (var sha = SHA256.Create())
                return Path.Combine(AppPaths.Data, "worker-sessions", harness, BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sessionId))).Replace("-", "") + ".json");
        }

        private static JObject ProbeExecutable(string id, string path, bool probe)
        {
            var result = new JObject { ["id"] = id, ["available"] = IsExistingExecutable(path), ["path"] = path ?? "" };
            if (!probe || !((bool)result["available"])) return result;
            try
            {
                var info = new ProcessStartInfo(path, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(info))
                {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(8000)) { try { process.Kill(); } catch { } result["probeOk"] = false; result["probeError"] = "版本探测超时"; }
                    else { System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { output, error }, 1500); result["probeOk"] = process.ExitCode == 0; result["version"] = Limit(output.Result, 300).Trim(); result["probeError"] = Limit(error.Result, 1000).Trim(); }
                }
            }
            catch (Exception error) { result["probeOk"] = false; result["probeError"] = Limit(error.Message, 1000); }
            return result;
        }

        private static string ConfiguredHarness()
        {
            return ((string)Settings()["workerHarness"] ?? "auto").Trim().ToLowerInvariant();
        }

        private static JArray CustomWorkers() { return Settings()["agentWorkers"] as JArray ?? new JArray(); }
        private static JObject Settings() { try { return JsonUtil.Read(AppPaths.SettingsFile, new JObject()) as JObject ?? new JObject(); } catch { return new JObject(); } }
        private static string FullExistingExecutable(string value)
        {
            var path = SafeFullPath(value);
            if (!IsExistingExecutable(path)) throw new FileNotFoundException("自定义 Agent Worker 可执行文件不存在。", path);
            return path;
        }
        private static bool IsExistingExecutable(string value) { try { return !string.IsNullOrWhiteSpace(value) && File.Exists(value) && new FileInfo(value).Length > 0; } catch { return false; } }
        private static string SafeFullPath(string value) { try { return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))); } catch { return ""; } }
        private static string ReplaceTemplate(string value, JObject request)
        {
            return value.Replace("{workspace}", (string)request["workspace"] ?? "")
                .Replace("{model}", (string)request["model"] ?? "")
                .Replace("{sessionId}", (string)request["sessionId"] ?? "")
                .Replace("{effort}", (string)request["effort"] ?? "");
        }
        private static string Limit(string value, int max) { value = value ?? ""; return value.Length <= max ? value : value.Substring(0, max); }
    }

}

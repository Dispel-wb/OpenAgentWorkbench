using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class AgentWorkerBridge
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly StreamWriter Output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true };

        internal static int Run(string configPath)
        {
            try
            {
                var config = JsonUtil.Read(configPath, null) as JObject;
                if ((string)config?["harness"] == "dsh") return DshWorkerBridge.Run(config);
                if ((string)config?["harness"] == "pi") return PiWorkerBridge.Run(config);
                if (config == null || (string)config["harness"] != "codex") throw new InvalidDataException("Agent Worker bridge configuration is invalid.");
                var statePath = (string)config["statePath"] ?? Path.ChangeExtension(configPath, ".state.json");
                var state = JsonUtil.Read(statePath, new JObject()) as JObject ?? new JObject();
                Write(new JObject { ["type"] = "system", ["subtype"] = "init", ["worker"] = "codex", ["session_id"] = config["sessionId"] });
                string line;
                using (var inputReader = new StreamReader(Console.OpenStandardInput(), Utf8, false, 4096, true))
                while ((line = inputReader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var input = JObject.Parse(line);
                    var prompt = InputText(input);
                    if (prompt.Length == 0) prompt = "继续";
                    var outcome = RunCodex(config, state, prompt);
                    state = outcome.State;
                    JsonUtil.WriteAtomic(statePath, state);
                    if (outcome.Text.Length > 0)
                        Write(new JObject { ["type"] = "assistant", ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = outcome.Text }) } });
                    Write(new JObject
                    {
                        ["type"] = "result", ["subtype"] = outcome.ExitCode == 0 ? "success" : "error",
                        ["is_error"] = outcome.ExitCode != 0, ["result"] = outcome.ExitCode == 0 ? outcome.Text : outcome.Error,
                        ["usage"] = outcome.Usage ?? new JObject(), ["worker"] = "codex"
                    });
                }
                return 0;
            }
            catch (Exception error)
            {
                Write(new JObject { ["type"] = "result", ["subtype"] = "error", ["is_error"] = true, ["result"] = error.Message, ["worker"] = "bridge" });
                return 1;
            }
        }

        private static BridgeOutcome RunCodex(JObject config, JObject state, string prompt)
        {
            var threadId = ((string)state["threadId"] ?? "").Trim();
            var arguments = new List<string> { "exec", "--json", "--color", "never", "--skip-git-repo-check", "-C", (string)config["workspace"] ?? Environment.CurrentDirectory };
            AddModel(arguments, config);
            var permission = ((string)config["permissionMode"] ?? "readonly").ToLowerInvariant();
            if (permission == "full") arguments.Add("--dangerously-bypass-approvals-and-sandbox");
            else { arguments.Add("--sandbox"); arguments.Add(permission == "readonly" || permission == "plan" ? "read-only" : "workspace-write"); }
            if (permission != "readonly" && permission != "plan")
                foreach (var directory in config["addDirs"] as JArray ?? new JArray()) { arguments.Add("--add-dir"); arguments.Add((string)directory ?? ""); }
            if (threadId.Length > 0) { arguments.Add("resume"); arguments.Add(threadId); }
            arguments.Add("-");
            var start = new ProcessStartInfo
            {
                FileName = (string)config["executable"], Arguments = string.Join(" ", arguments.Select(NativeWorkerHandle.QuoteArgument)),
                WorkingDirectory = (string)config["workspace"] ?? Environment.CurrentDirectory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
            };
            var text = new StringBuilder(); var errors = new StringBuilder(); JObject usage = null;
            var terminalSeen = false; var terminalFailed = false;
            using (var process = Process.Start(start))
            using (var job = NativeJobObject.Attach(process))
            {
                var errorTask = System.Threading.Tasks.Task.Run(async () =>
                {
                    var tail = new StringBuilder(); var buffer = new char[4096]; int count;
                    while ((count = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    { tail.Append(buffer, 0, count); if (tail.Length > 16384) tail.Remove(0, tail.Length - 16384); }
                    return tail.ToString();
                });
                var promptBytes = Utf8.GetBytes(prompt);
                process.StandardInput.BaseStream.Write(promptBytes, 0, promptBytes.Length);
                process.StandardInput.BaseStream.Flush(); process.StandardInput.Close();
                string line;
                while ((line = SdkFrameReader.ReadLine(process.StandardOutput)) != null)
                {
                    JObject value;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { value = JObject.Parse(line); }
                    catch (JsonException parseError) { throw new InvalidDataException("Codex 核心输出了无效 JSON，已停止当前任务。", parseError); }
                    if ((string)value["type"] == "thread.started" && !string.IsNullOrWhiteSpace((string)value["thread_id"]))
                    { threadId = (string)value["thread_id"]; state["threadId"] = threadId; if (!string.IsNullOrWhiteSpace((string)config["statePath"])) JsonUtil.WriteAtomic((string)config["statePath"], state); }
                    var item = value["item"] as JObject;
                    if ((string)value["type"] == "item.completed" && (string)item?["type"] == "agent_message")
                    {
                        var message = (string)item["text"] ?? "";
                        if ((long)text.Length + message.Length > 8 * 1024 * 1024)
                            throw new InvalidDataException("Codex 本轮回答超过安全长度上限，已停止当前任务。");
                        text.Append(message);
                    }
                    if ((string)value["type"] == "turn.completed" && value["usage"] is JObject)
                        usage = CodexTurnUsage((JObject)value["usage"], state);
                    if ((string)value["type"] == "turn.completed") terminalSeen = true;
                    if ((string)value["type"] == "turn.failed") { terminalSeen = true; terminalFailed = true; }
                    if ((string)value["type"] == "error" || (string)value["type"] == "turn.failed") errors.AppendLine((string)value["error"]?["message"] ?? (string)value["message"] ?? line);
                    if (errors.Length > 16384) errors.Remove(0, errors.Length - 16384);
                    EmitCodexTool(value, item);
                    Write(new JObject { ["type"] = "system", ["subtype"] = "worker_event", ["worker"] = "codex", ["event"] = value });
                    // A completed turn is the protocol boundary. Some CLI versions keep
                    // pipes/processes alive during shutdown; do not wait for EOF forever.
                    if (terminalSeen) break;
                }
                var cleanupAfterTerminal = false;
                if (!process.WaitForExit(5000))
                {
                    cleanupAfterTerminal = terminalSeen;
                    job.Dispose();
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.WaitForExit(2000);
                    if (cleanupAfterTerminal) Write(new JObject { ["type"] = "system", ["subtype"] = "worker_cleanup", ["worker"] = "codex", ["message"] = "核心已返回本轮终态；已清理未正常退出的独立子进程。" });
                }
                try { if (errorTask.Wait(1000)) errors.Append(errorTask.Result); } catch { }
                var error = errors.ToString().Trim();
                var exitCode = terminalFailed || !terminalSeen ? 1 : cleanupAfterTerminal ? 0 : process.HasExited ? process.ExitCode : 1;
                if (exitCode != 0 && error.Length == 0) error = !terminalSeen ? "Codex 核心已退出，但没有返回本轮完成事件。" : "Codex Worker exited with code " + exitCode + ".";
                return new BridgeOutcome { ExitCode = exitCode, Text = text.ToString(), Error = error, Usage = usage, State = state };
            }
        }

        private static void AddModel(List<string> arguments, JObject config) { var model = ((string)config["model"] ?? "").Trim(); if (model.Length > 0) { arguments.Add("--model"); arguments.Add(model); } }
        private static JObject CodexTurnUsage(JObject cumulative, JObject state)
        {
            // Codex exec reports thread totals on resume. The workbench footer reports this turn only.
            var previous = state["cumulativeUsage"] as JObject ?? new JObject();
            Func<string, long> delta = key => Math.Max(0L, ((long?)cumulative[key] ?? 0L) - ((long?)previous[key] ?? 0L));
            var input = delta("input_tokens"); var cached = delta("cached_input_tokens");
            var result = new JObject { ["input_tokens"] = Math.Max(0L, input - cached), ["output_tokens"] = delta("output_tokens"), ["cache_read_input_tokens"] = cached };
            state["cumulativeUsage"] = cumulative.DeepClone();
            return result;
        }
        private static void EmitCodexTool(JObject value, JObject item)
        {
            var type = (string)item?["type"];
            if (type != "command_execution" && type != "file_change" && type != "mcp_tool_call") return;
            var id = (string)item["id"];
            if (string.IsNullOrWhiteSpace(id)) return;
            if ((string)value["type"] == "item.started" || (string)value["type"] == "item.completed")
                Write(new JObject { ["type"] = "assistant", ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "tool_use", ["id"] = id, ["name"] = type == "command_execution" ? "Bash" : type == "file_change" ? "Edit" : (string)item["tool"] ?? "MCP", ["input"] = new JObject { ["command"] = item["command"], ["changes"] = item["changes"], ["arguments"] = item["arguments"] } }) } });
            if ((string)value["type"] == "item.completed")
                Write(new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["is_error"] = (string)item["status"] == "failed" || ((int?)item["exit_code"] ?? 0) != 0, ["content"] = (string)item["aggregated_output"] ?? item["result"]?.ToString(Formatting.None) ?? item["changes"]?.ToString(Formatting.None) ?? "" }) } });
        }
        private static string InputText(JObject input)
        {
            var content = input["message"]?["content"] as JArray;
            return string.Join("\n", (content ?? new JArray()).OfType<JObject>().Where(item => (string)item["type"] == "text").Select(item => (string)item["text"] ?? ""));
        }
        private static void Write(JObject value) { Output.WriteLine(value.ToString(Formatting.None)); }

        private sealed class BridgeOutcome { internal int ExitCode; internal string Text = ""; internal string Error = ""; internal JObject Usage; internal JObject State; }
    }
}

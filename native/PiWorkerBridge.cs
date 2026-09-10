using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    // Pi RPC is JSONL, not JSON-RPC 2.0. Acceptance and agent_end are not completion.
    internal static class PiWorkerBridge
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private const int Limit = 8 * 1024 * 1024;

        internal static int Run(JObject config)
        {
            using (var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true })
            using (var input = new StreamReader(Console.OpenStandardInput(), Utf8))
            {
                Action<JObject> emit = value => output.WriteLine(value.ToString(Formatting.None));
                try
                {
                    var mode = ((string)config["permissionMode"] ?? "readonly").Trim().ToLowerInvariant();
                    if (!new[] { "readonly", "plan", "full" }.Contains(mode)) throw new InvalidDataException("Pi 不支持此权限模式；未启动核心。");
                    emit(new JObject { ["type"] = "system", ["subtype"] = "init", ["worker"] = "pi", ["session_id"] = config["sessionId"] });
                    string line;
                    while ((line = SdkFrameReader.ReadLine(input)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var message = JObject.Parse(line);
                        var content = message["message"]?["content"] as JArray ?? new JArray();
                        if (content.Any(item => (string)item["type"] != "text")) throw new InvalidDataException("Pi 适配器暂仅支持文字输入；没有忽略附件继续执行。");
                        var prompt = string.Join("\n", content.Select(item => (string)item["text"] ?? ""));
                        if (prompt.Length == 0) throw new InvalidDataException("Pi 输入不能为空。");
                        // Each workbench turn gets a fresh process with the native persisted session.
                        // This avoids stale idle events and makes cancellation/permission changes explicit.
                        RunTurn(config, mode, prompt, emit);
                    }
                    return 0;
                }
                catch (Exception error)
                {
                    emit(new JObject { ["type"] = "result", ["subtype"] = "error", ["is_error"] = true, ["result"] = error.Message, ["worker"] = "pi" });
                    return 1;
                }
            }
        }

        private static ProcessStartInfo PrepareStart(JObject config, string mode)
        {
            var statePath = (string)config["statePath"];
            var state = JsonUtil.Read(statePath, new JObject()) as JObject ?? new JObject();
            var sessionFile = (string)state["sessionFile"] ?? "";
            var sessionDirectory = Path.Combine(Path.GetDirectoryName(statePath), Path.GetFileNameWithoutExtension(statePath));
            Directory.CreateDirectory(sessionDirectory);
            var args = new List<string> { (string)config["entry"], "--mode", "rpc", "--provider", "workbench", "--model", (string)config["model"],
                "--thinking", "off", "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-themes", "--no-context-files", "--no-approve", "--offline",
                "--session-dir", sessionDirectory };
            if (sessionFile.Length > 0)
            {
                if (!File.Exists(sessionFile)) throw new InvalidDataException("Pi 会话文件已丢失；未静默创建无历史会话。");
                args.Add("--session"); args.Add(sessionFile);
            }
            if (mode != "full") args.Add("--no-tools");
            else { args.Add("--tools"); args.Add("read,bash,edit,write,grep,find,ls"); }
            var instructions = (string)config["trustedInstructionPath"] ?? "";
            if (instructions.Length > 0) { args.Add("--append-system-prompt"); args.Add(instructions); }
            var start = new ProcessStartInfo {
                FileName = (string)config["executable"], Arguments = string.Join(" ", args.Select(NativeWorkerHandle.QuoteArgument)),
                WorkingDirectory = (string)config["workspace"], UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
            start.EnvironmentVariables["PI_CODING_AGENT_DIR"] = (string)config["home"];
            start.EnvironmentVariables["PI_OFFLINE"] = "1";
            start.EnvironmentVariables["PI_TELEMETRY"] = "0";
            return start;
        }

        private static void RunTurn(JObject config, string mode, string prompt, Action<JObject> emit)
        {
            var statePath = (string)config["statePath"];
            var start = PrepareStart(config, mode);
            using (var process = Process.Start(start))
            using (var job = NativeJobObject.Attach(process))
            using (var writer = new StreamWriter(process.StandardInput.BaseStream, Utf8) { AutoFlush = true, NewLine = "\n" })
            {
                // Drain stderr without accumulating unbounded output or exposing provider credentials.
                var drain = Task.Run(async () => { var buffer = new char[4096]; while (await process.StandardError.ReadAsync(buffer, 0, buffer.Length) > 0) { } });
                Action<JObject> send = value => writer.WriteLine(value.ToString(Formatting.None));
                send(new JObject { ["id"] = "state", ["type"] = "get_state" });
                var accepted = false; var started = false; var settled = false; var turns = 0; var sawAssistant = false;
                var answer = new StringBuilder(); var failure = "";
                var usage = new JObject { ["input_tokens"] = 0L, ["output_tokens"] = 0L, ["cache_read_input_tokens"] = 0L, ["cache_creation_input_tokens"] = 0L };
                string line;
                while ((line = SdkFrameReader.ReadLine(process.StandardOutput)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject value;
                    try { value = JObject.Parse(line); }
                    catch (JsonException error) { throw new InvalidDataException("Pi 输出了无效 JSON。", error); }
                    var type = (string)value["type"];
                    if (type == "response")
                    {
                        if ((bool?)value["success"] != true) throw new InvalidDataException("Pi RPC 请求失败：" + ((string)value["error"] ?? "未知错误"));
                        if ((string)value["id"] == "state")
                        {
                            if (started) throw new InvalidDataException("Pi 返回重复启动响应。");
                            var sessionFile = (string)value["data"]?["sessionFile"] ?? "";
                            if (sessionFile.Length == 0) throw new InvalidDataException("Pi 未提供可持久化会话路径。");
                            // Save before prompting, so an interrupted turn is never silently replayed.
                            JsonUtil.WriteAtomic(statePath, new JObject { ["sessionFile"] = sessionFile, ["sessionId"] = value["data"]?["sessionId"] });
                            started = true;
                            send(new JObject { ["id"] = "prompt", ["type"] = "prompt", ["message"] = prompt });
                        }
                        if ((string)value["id"] == "prompt") accepted = true;
                    }
                    else if (type == "message_update")
                    {
                        var delta = value["assistantMessageEvent"];
                        if ((string)delta?["type"] == "text_delta") emit(new JObject { ["type"] = "stream_event", ["event"] = new JObject {
                            ["type"] = "content_block_delta", ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = delta["delta"] } } });
                    }
                    else if (type == "message_end" && (string)value["message"]?["role"] == "assistant")
                    {
                        sawAssistant = true;
                        var message = value["message"];
                        var text = string.Join("", (message["content"] as JArray ?? new JArray()).Where(item => (string)item["type"] == "text").Select(item => (string)item["text"] ?? ""));
                        if ((long)answer.Length + text.Length > Limit) throw new InvalidDataException("Pi 本轮回答超过安全长度上限。");
                        answer.Append(text);
                        if (text.Length > 0) emit(new JObject { ["type"] = "assistant", ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) } });
                        failure = new[] { "error", "aborted" }.Contains((string)message["stopReason"]) ? ((string)message["errorMessage"] ?? "Pi 模型调用失败或被取消。") : "";
                        var rawUsage = message["usage"];
                        var names = new[] { "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens" };
                        var piNames = new[] { "input", "output", "cacheRead", "cacheWrite" };
                        for (var index = 0; index < names.Length; index++) usage[names[index]] = (long)usage[names[index]] + Math.Max(0L, (long?)rawUsage?[piNames[index]] ?? 0L);
                    }
                    else if (type == "tool_execution_start")
                    {
                        if (mode != "full") throw new InvalidDataException("Pi 在禁用工具模式发起工具执行，已停止。");
                        emit(new JObject { ["type"] = "assistant", ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject {
                            ["type"] = "tool_use", ["id"] = value["toolCallId"], ["name"] = value["toolName"], ["input"] = value["args"] }) } });
                    }
                    else if (type == "tool_execution_end")
                        emit(new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject {
                            ["type"] = "tool_result", ["tool_use_id"] = value["toolCallId"], ["is_error"] = value["isError"] ?? false,
                            ["content"] = value["result"]?["content"]?.ToString(Formatting.None) ?? "" }) } });
                    else if (type == "extension_ui_request" && new[] { "confirm", "select", "input", "editor" }.Contains((string)value["method"]))
                        throw new InvalidDataException("Pi 请求交互授权或输入；当前适配器不支持，未自动批准。");
                    else if (type == "turn_start" && ++turns > Math.Max(1, (int?)config["maxTurns"] ?? 100))
                        throw new InvalidDataException("Pi 达到本轮最大执行轮数。");
                    else if (type == "agent_settled") { settled = true; break; }
                }
                if (!settled || !started || !sawAssistant) throw new InvalidDataException("Pi 核心退出但未返回完整终态，未报告成功。");
                // Prompt responses may arrive after agent_settled on some RPC versions; the settled
                // event and assistant message, not mere command acceptance, define completion.
                if (!accepted) emit(new JObject { ["type"] = "system", ["subtype"] = "worker_event", ["worker"] = "pi", ["message"] = "Pi 已返回完整终态。" });
                if (failure.Length > 0) throw new InvalidDataException(failure);
                job.Dispose();
                if (!process.WaitForExit(2000)) { try { process.Kill(); } catch { } }
                emit(new JObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = answer.ToString(), ["usage"] = usage, ["worker"] = "pi" });
            }
        }
    }
}

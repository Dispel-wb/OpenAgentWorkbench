using System;
using System.Collections.Concurrent;
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
    // Drives the official `dsh --profile sdk` core, not an imitation Agent loop.
    internal static class DshWorkerBridge
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static int Run(JObject config)
        {
            var output = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true };
            Action<JObject> emit = value => output.WriteLine(value.ToString(Formatting.None));
            try
            {
                using (var peer = new DshPeer(config, emit))
                using (var input = new StreamReader(Console.OpenStandardInput(), Utf8, false, 4096, true))
                {
                    peer.Initialize();
                    emit(new JObject { ["type"] = "system", ["subtype"] = "init", ["worker"] = "dsh", ["session_id"] = config["sessionId"] });
                    string line;
                    while ((line = input.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var request = JObject.Parse(line);
                        var blocks = request["message"]?["content"] as JArray ?? new JArray();
                        peer.Prompt(new JArray(blocks.OfType<JObject>().Where(value => (string)value["type"] == "text").Select(value => value.DeepClone())));
                    }
                }
                return 0;
            }
            catch (Exception error)
            {
                emit(new JObject { ["type"] = "result", ["subtype"] = "error", ["is_error"] = true, ["result"] = "DSHarness 核心：" + error.Message, ["worker"] = "dsh" });
                return 1;
            }
        }

        private sealed class DshPeer : IDisposable
        {
            private readonly JObject _config;
            private readonly Action<JObject> _emit;
            private readonly Process _process;
            private readonly NativeJobObject _job;
            private readonly BlockingCollection<JObject> _frames = new BlockingCollection<JObject>(2048);
            private readonly StreamWriter _writer;
            private readonly Queue<JObject> _pending = new Queue<JObject>();
            private int _nextId;
            private bool _disposed;

            internal DshPeer(JObject config, Action<JObject> emit)
            {
                _config = config; _emit = emit;
                var args = new List<string> { (string)config["entry"], "--profile", (string)config["profile"] ?? "sdk" };
                if (!string.IsNullOrWhiteSpace((string)config["patchPath"])) { args.Add("--patch"); args.Add((string)config["patchPath"]); }
                var start = new ProcessStartInfo((string)config["executable"], string.Join(" ", args.Select(NativeWorkerHandle.QuoteArgument)))
                {
                    WorkingDirectory = (string)config["workspace"], UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
                };
                start.EnvironmentVariables["DSH_HOME"] = (string)config["home"];
                start.EnvironmentVariables["DSH_TELEMETRY_DISABLED"] = "1";
                start.EnvironmentVariables["DSH_TELEMETRY_MODE"] = "DISABLED";
                start.EnvironmentVariables["DSH_MAX_TOKENS_AS_SUCCESS"] = "false";
                var mode = (string)config["permissionMode"] ?? "readonly";
                start.EnvironmentVariables["DSH_PERMISSION_MODE"] = mode == "full" ? "danger-full-access" : mode == "readonly" || mode == "plan" ? "read-only" : "workspace-write";
                _process = Process.Start(start);
                _job = NativeJobObject.Attach(_process);
                _writer = new StreamWriter(_process.StandardInput.BaseStream, Utf8, 4096, true) { AutoFlush = true };
                Task.Run(() =>
                {
                    try
                    {
                        string line;
                        while ((line = _process.StandardOutput.ReadLine()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("SDK frame exceeds 8 MiB.");
                            _frames.Add(JObject.Parse(line));
                        }
                        _frames.Add(new JObject { ["transportError"] = "DSHarness SDK 输出管道已关闭。" });
                    }
                    catch (Exception error) { try { _frames.Add(new JObject { ["transportError"] = error.Message }); } catch { } }
                });
                // Keep stderr drained without accumulating an unbounded diagnostic buffer.
                Task.Run(() => { try { var buffer = new char[4096]; int count; using (var error = new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true }) while ((count = _process.StandardError.Read(buffer, 0, buffer.Length)) > 0) error.Write(buffer, 0, count); } catch { } });
            }

            internal void Initialize()
            {
                var response = Request("initialize", new JObject { ["cwd"] = _config["workspace"], ["provider"] = "deepseek-official", ["model"] = _config["model"] }, 30000);
                if ((string)response?["serverInfo"]?["name"] != "deepseek-harness-sdk-runtime")
                    throw new InvalidDataException("所选进程不是兼容的 DSHarness SDK 核心。");
            }

            internal void Prompt(JArray blocks)
            {
                var sessionId = (string)_config["sessionId"];
                var result = Request("session/prompt", new JObject { ["sessionId"] = sessionId, ["contentBlocks"] = blocks }, 30000);
                var messageId = (string)result?["messageId"];
                if (string.IsNullOrWhiteSpace(messageId)) throw new InvalidDataException("DSHarness 未返回输入入队凭据。");
                var received = false; var final = ""; JObject reason = null; var usage = new JObject(); var steps = 0;
                while (true)
                {
                    var frame = _pending.Count > 0 ? _pending.Dequeue() : ReadFrame(120000);
                    var method = (string)frame["method"]; var data = frame["params"] as JObject;
                    if ((string)data?["sessionId"] != sessionId) continue;
                    var ev = data?["event"] as JObject;
                    if (!received)
                    {
                        received = method == "session.event" && (string)ev?["type"] == "agent/inbox/spliced" &&
                            (ev?["data"]?["inserted"] as JArray ?? new JArray()).OfType<JObject>().Any(value => (string)value["id"] == messageId);
                        if (!received) continue;
                    }
                    _emit(new JObject { ["type"] = "system", ["subtype"] = "worker_event", ["worker"] = "dsh", ["event"] = frame });
                    if (method == "session.event")
                    {
                        var body = ev?["data"] as JObject ?? new JObject();
                        switch ((string)ev?["type"])
                        {
                            case "step/start":
                                if (++steps > Math.Max(10, Math.Min(500, (int?)_config["maxTurns"] ?? 100))) throw new InvalidOperationException("已达到本轮 DSHarness 执行步骤上限。");
                                break;
                            case "assistant/chunk":
                                var chunk = body["chunk"] as JObject;
                                if ((string)chunk?["type"] == "text-delta") _emit(new JObject { ["type"] = "stream_event", ["event"] = new JObject { ["type"] = "content_block_delta", ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = chunk["text"] } } });
                                break;
                            case "assistant/message":
                                final = Text(body["message"]?["content"]);
                                AddUsage(usage, body["usage"] as JObject);
                                break;
                            case "tool/call":
                                JObject arguments; try { arguments = JObject.Parse((string)body["arguments"] ?? "{}"); } catch { arguments = new JObject { ["raw"] = body["arguments"] }; }
                                _emit(new JObject { ["type"] = "assistant", ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "tool_use", ["id"] = body["callId"], ["name"] = body["name"], ["input"] = arguments }) } });
                                break;
                            case "tool/result":
                                var block = (body["message"]?["content"] as JArray)?.First as JObject;
                                _emit(new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "tool_result", ["tool_use_id"] = block?["toolCallId"], ["is_error"] = block?["isError"] ?? false, ["content"] = Text(block?["content"]) }) } });
                                break;
                            case "approval/asked":
                                throw new InvalidOperationException("核心请求了额外权限；当前 SDK 没有审批响应接口，已安全停止。请调整权限后重新执行，未自动放行。");
                            case "turn/end": reason = body["reason"] as JObject; break;
                        }
                    }
                    if (method == "session.status" && (string)data["status"] == "idle") break;
                }
                var success = (string)reason?["kind"] == "completed";
                var text = success ? final : (string)reason?["error"]?["message"] ?? "DSHarness 任务未成功完成：" + ((string)reason?["kind"] ?? "缺少结束事件");
                _emit(new JObject { ["type"] = "result", ["subtype"] = success ? "success" : "error", ["is_error"] = !success, ["result"] = text, ["usage"] = usage, ["worker"] = "dsh" });
            }

            private JObject Request(string method, JObject parameters, int timeout)
            {
                var id = ++_nextId;
                _writer.WriteLine(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToString(Formatting.None));
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    var frame = ReadFrame(Math.Max(1, timeout - (int)timer.ElapsedMilliseconds));
                    if ((string)frame["id"] == id.ToString())
                    {
                        if (frame["error"] != null) throw new InvalidOperationException((string)frame["error"]?["message"] ?? "SDK 请求失败");
                        return frame["result"] as JObject;
                    }
                    if (_pending.Count >= 2048) throw new InvalidDataException("SDK 在确认请求前输出了过量事件。");
                    _pending.Enqueue(frame);
                    if (timer.ElapsedMilliseconds >= timeout) throw new TimeoutException("DSHarness SDK 请求超时：" + method);
                }
            }

            private JObject ReadFrame(int timeout)
            {
                JObject frame;
                if (!_frames.TryTake(out frame, timeout)) throw new TimeoutException("DSHarness SDK 未在规定时间内响应。");
                if (frame["transportError"] != null) throw new IOException((string)frame["transportError"]);
                return frame;
            }
            private static string Text(JToken blocks) { return string.Join("", (blocks as JArray ?? new JArray()).OfType<JObject>().Where(value => (string)value["type"] == "text").Select(value => (string)value["text"] ?? "")); }
            private static void AddUsage(JObject total, JObject usage)
            {
                if (usage == null) return;
                foreach (var pair in new[] { new[] { "inputTokens", "input_tokens" }, new[] { "outputTokens", "output_tokens" }, new[] { "cacheReadTokens", "cache_read_input_tokens" }, new[] { "cacheWriteTokens", "cache_creation_input_tokens" } })
                    if (usage[pair[0]] != null) total[pair[1]] = ((long?)total[pair[1]] ?? 0) + ((long?)usage[pair[0]] ?? 0);
                // The normalized Claude-style input field excludes separately counted cache tokens.
                total["input_tokens"] = Math.Max(0L, ((long?)total["input_tokens"] ?? 0) - ((long?)usage["cacheReadTokens"] ?? 0) - ((long?)usage["cacheWriteTokens"] ?? 0));
            }
            public void Dispose()
            {
                if (_disposed) return; _disposed = true;
                try { if (!_process.HasExited) Request("shutdown", new JObject(), 3000); } catch { }
                try { _writer.Dispose(); _process.StandardInput.Close(); } catch { }
                try { if (!_process.WaitForExit(1500)) _process.Kill(); } catch { }
                _job?.Dispose(); _process.Dispose();
            }
        }
    }
}

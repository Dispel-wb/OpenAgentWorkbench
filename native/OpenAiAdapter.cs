using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class OpenAiAdapter
    {
        private static readonly TimeSpan HeaderTimeout = ReadTimeout("CLAUDE_GUI_ADAPTER_HEADER_TIMEOUT_SECONDS", 45);
        private static readonly TimeSpan StreamIdleTimeout = ReadTimeout("CLAUDE_GUI_ADAPTER_IDLE_TIMEOUT_SECONDS", 60);
        private const int MaximumJsonBodyBytes = 16 * 1024 * 1024;
        private const int MaximumErrorBodyBytes = 2 * 1024 * 1024;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<CancellationTokenSource, byte>> ActiveRequests =
            new ConcurrentDictionary<string, ConcurrentDictionary<CancellationTokenSource, byte>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 32
        }) { Timeout = Timeout.InfiniteTimeSpan };

        public static string Endpoint(string baseUrl, string suffix)
        {
            var value = (baseUrl ?? "").TrimEnd('/');
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value;
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return value + suffix.Substring(3);
            var lastSegment = value.Substring(value.LastIndexOf('/') + 1);
            if (lastSegment.Length > 1 && (lastSegment[0] == 'v' || lastSegment[0] == 'V') && lastSegment.Skip(1).All(char.IsDigit))
                return value + suffix.Substring(3);
            return value + suffix;
        }

        public static void ApplyAuth(HttpRequestMessage request, string token, string style)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (style == "x-api-key") request.Headers.TryAddWithoutValidation("x-api-key", token);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public static JObject CountTokens(JObject payload)
        {
            var text = JsonUtil.Compact(payload["messages"] ?? new JArray()) + TextFromBlocks(payload["system"]);
            return new JObject { ["input_tokens"] = Math.Max(1, text.Length / 4) };
        }

        public static async Task<JObject> HandleAsync(HttpListenerContext context, JObject provider, string token, JObject payload, string runId)
        {
            JObject openAi = null;
            var config = provider["text"] as JObject ?? new JObject();
            var url = Endpoint((string)config["baseUrl"], "/v1/chat/completions");
            var streaming = (bool?)payload["stream"] ?? false;
            var configuredAuthStyle = ((string)provider["authStyle"] ?? "auto").Trim().ToLowerInvariant();
            var authStyles = configuredAuthStyle == "auto" ? new[] { "bearer", "x-api-key" } : new[] { configuredAuthStyle };
            var requestCancellation = new CancellationTokenSource();
            Task<HttpResponseMessage> sendTask = null;
            Register(runId, requestCancellation);
            var streamStarted = false;
            try
            {
                openAi = AnthropicToOpenAi(payload);
                if (streaming)
                {
                    PrepareEventStream(context.Response);
                    streamStarted = true;
                    await WriteEvent(context.Response, "ping", new JObject { ["type"] = "ping" }, requestCancellation.Token);
                }

                for (var authIndex = 0; authIndex < authStyles.Length; authIndex++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        ApplyAuth(request, token, authStyles[authIndex]);
                        request.Content = new StringContent(openAi.ToString(Formatting.None), Encoding.UTF8, "application/json");

                        sendTask = Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
                        using (var upstream = await WaitForHeaders(sendTask, context.Response, streaming, requestCancellation))
                        {
                            if (!upstream.IsSuccessStatusCode)
                            {
                                var rawError = await ReadContentLimitedAsync(upstream.Content, MaximumErrorBodyBytes, StreamIdleTimeout,
                                    requestCancellation.Token, "上游错误响应正文读取超时");
                                var retryAuth = configuredAuthStyle == "auto" && authIndex + 1 < authStyles.Length &&
                                    (upstream.StatusCode == HttpStatusCode.Unauthorized || upstream.StatusCode == HttpStatusCode.Forbidden);
                                if (retryAuth) continue;
                                if (openAi["stream_options"] != null &&
                                    (upstream.StatusCode == HttpStatusCode.BadRequest || (int)upstream.StatusCode == 422) &&
                                    rawError.IndexOf("stream_options", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    // Several OpenAI-compatible gateways implement streaming but reject
                                    // the newer usage extension. Retry the same auth once without that
                                    // optional field; generation has not started on a schema error.
                                    openAi.Remove("stream_options");
                                    authIndex--;
                                    continue;
                                }
                                var error = UpstreamError(upstream.StatusCode, rawError, token);
                                if (streaming) await WriteEvent(context.Response, "error", error, requestCancellation.Token);
                                else await ApiServer.WriteJsonAsync(context.Response, error, (int)upstream.StatusCode);
                                return error;
                            }
                            if (streaming) await StreamAsAnthropic(context.Response, upstream, (string)payload["model"] ?? "", requestCancellation);
                            else
                            {
                                var rawBody = await ReadContentLimitedAsync(upstream.Content, MaximumJsonBodyBytes, StreamIdleTimeout,
                                    requestCancellation.Token, "上游模型响应正文读取超时");
                                JObject body;
                                try { body = JObject.Parse(rawBody); }
                                catch { throw new InvalidDataException("上游非流式响应不是有效 JSON"); }
                                await ApiServer.WriteJsonAsync(context.Response, NonStreamAsAnthropic(body, (string)payload["model"] ?? ""));
                            }
                            return null;
                        }
                    }
                }
                throw new InvalidOperationException("没有可用的 API 鉴权方式");
            }
            catch (OperationCanceledException)
            {
                if (requestCancellation.IsCancellationRequested) return null;
                throw;
            }
            catch (TimeoutException error)
            {
                requestCancellation.Cancel();
                var body = AdapterError("timeout_error", error.Message);
                if (streamStarted)
                {
                    try { await WriteEvent(context.Response, "error", body); } catch { }
                }
                else await ApiServer.WriteJsonAsync(context.Response, body, 504);
                return body;
            }
            catch (Exception error) when (IsClientDisconnect(error))
            {
                requestCancellation.Cancel();
                return null;
            }
            catch (Exception error)
            {
                requestCancellation.Cancel();
                var body = AdapterError("api_error", "OpenAI 兼容接口响应处理失败：" +
                    Limit(Sanitize(FlattenMessage(error), token), 1800));
                if (streamStarted)
                {
                    try { await WriteEvent(context.Response, "error", body); } catch { }
                }
                else
                {
                    try { await ApiServer.WriteJsonAsync(context.Response, body, 502); } catch { }
                }
                return body;
            }
            finally
            {
                if (requestCancellation.IsCancellationRequested) await DrainCancelledRequest(sendTask);
                Unregister(runId, requestCancellation);
                requestCancellation.Dispose();
            }
        }

        public static void CancelRun(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            ConcurrentDictionary<CancellationTokenSource, byte> requests;
            if (!ActiveRequests.TryRemove(runId, out requests)) return;
            foreach (var cancellation in requests.Keys)
            {
                try { cancellation.Cancel(); } catch { }
            }
        }

        private static void Register(string runId, CancellationTokenSource cancellation)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            ActiveRequests.GetOrAdd(runId, _ => new ConcurrentDictionary<CancellationTokenSource, byte>())[cancellation] = 0;
        }

        private static void Unregister(string runId, CancellationTokenSource cancellation)
        {
            if (string.IsNullOrWhiteSpace(runId)) return;
            ConcurrentDictionary<CancellationTokenSource, byte> requests;
            if (!ActiveRequests.TryGetValue(runId, out requests)) return;
            byte ignored;
            requests.TryRemove(cancellation, out ignored);
            if (requests.IsEmpty) ActiveRequests.TryRemove(runId, out requests);
        }

        private static async Task DrainCancelledRequest(Task<HttpResponseMessage> sendTask)
        {
            if (sendTask == null) return;
            try
            {
                var completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(2)));
                if (completed != sendTask) return;
                var response = await sendTask;
                if (response != null) response.Dispose();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (HttpRequestException) { }
        }

        private static async Task<HttpResponseMessage> WaitForHeaders(Task<HttpResponseMessage> sendTask, HttpListenerResponse response,
            bool streaming, CancellationTokenSource cancellation)
        {
            var started = DateTime.UtcNow;
            while (!sendTask.IsCompleted)
            {
                Task completed;
                using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    var delay = Task.Delay(HeartbeatInterval, delayCancellation.Token);
                    completed = await Task.WhenAny(sendTask, delay);
                    if (completed == sendTask) delayCancellation.Cancel();
                }
                if (completed == sendTask) break;
                cancellation.Token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - started >= HeaderTimeout)
                {
                    cancellation.Cancel();
                    throw new TimeoutException("上游模型在 " + Math.Ceiling(HeaderTimeout.TotalSeconds) + " 秒内没有返回响应头，请检查接口地址、模型状态或稍后重试");
                }
                if (streaming) await WriteEvent(response, "ping", new JObject { ["type"] = "ping" }, cancellation.Token);
            }
            return await sendTask;
        }

        internal static async Task<string> ReadContentLimitedAsync(HttpContent content, int maximumBytes, TimeSpan idleTimeout,
            CancellationToken cancellation, string timeoutMessage)
        {
            if (content == null) return "";
            var declaredLength = content.Headers.ContentLength;
            if (declaredLength.HasValue && declaredLength.Value > maximumBytes)
                throw new InvalidDataException("上游响应正文超过安全上限（" + maximumBytes + " bytes）");

            using (var stream = await content.ReadAsStreamAsync())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellation);
                    Task completed;
                    using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        var delay = Task.Delay(idleTimeout, delayCancellation.Token);
                        completed = await Task.WhenAny(readTask, delay);
                        if (completed == readTask) delayCancellation.Cancel();
                    }
                    if (completed != readTask)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        throw new TimeoutException(timeoutMessage ?? "上游响应正文读取超时");
                    }
                    var count = await readTask;
                    if (count <= 0) break;
                    if (memory.Length + count > maximumBytes)
                        throw new InvalidDataException("上游响应正文超过安全上限（" + maximumBytes + " bytes）");
                    memory.Write(buffer, 0, count);
                }
                return new UTF8Encoding(false, true).GetString(memory.ToArray());
            }
        }

        private static JObject AnthropicToOpenAi(JObject payload)
        {
            var messages = new JArray();
            var system = TextFromBlocks(payload["system"]);
            if (system.Length > 0) messages.Add(new JObject { ["role"] = "system", ["content"] = system });
            foreach (var source in (payload["messages"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var role = (string)source["role"] ?? "user";
                var content = source["content"];
                if (content == null || content.Type == JTokenType.String)
                {
                    messages.Add(new JObject { ["role"] = role, ["content"] = (string)content ?? "" });
                    continue;
                }
                var blocks = content as JArray ?? new JArray();
                var textParts = blocks.OfType<JObject>().Where(item => (string)item["type"] == "text").Select(item => (string)item["text"] ?? "").ToArray();
                var toolUses = blocks.OfType<JObject>().Where(item => (string)item["type"] == "tool_use").ToArray();
                var toolResults = blocks.OfType<JObject>().Where(item => (string)item["type"] == "tool_result").ToArray();

                if (role == "assistant" && toolUses.Length > 0)
                {
                    var assistant = new JObject { ["role"] = "assistant", ["content"] = textParts.Length == 0 ? JValue.CreateNull() : new JValue(string.Join("\n", textParts)) };
                    assistant["tool_calls"] = new JArray(toolUses.Select(item => new JObject
                    {
                        ["id"] = (string)item["id"] ?? "call_" + Guid.NewGuid().ToString("N").Substring(0, 20),
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = (string)item["name"] ?? "tool",
                            ["arguments"] = JsonUtil.Compact(item["input"] ?? new JObject())
                        }
                    }));
                    messages.Add(assistant);
                }
                else
                {
                    var openAiContent = OpenAiContentFromAnthropicBlocks(blocks);
                    if (openAiContent != null) messages.Add(new JObject { ["role"] = role, ["content"] = openAiContent });
                }

                foreach (var result in toolResults)
                {
                    messages.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = (string)result["tool_use_id"] ?? "",
                        ["content"] = TextFromBlocks(result["content"])
                    });
                }
            }

            var output = new JObject
            {
                ["model"] = payload["model"],
                ["messages"] = messages,
                ["stream"] = (bool?)payload["stream"] ?? false,
                ["temperature"] = payload["temperature"] ?? 0.2
            };
            if (payload["max_tokens"] != null) output["max_tokens"] = payload["max_tokens"];
            if (payload["stop_sequences"] is JArray stops && stops.Count > 0) output["stop"] = stops;
            if (payload["tools"] is JArray tools && tools.Count > 0)
            {
                output["tools"] = new JArray(tools.OfType<JObject>().Select(tool => new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool["name"],
                        ["description"] = (string)tool["description"] ?? "",
                        ["parameters"] = tool["input_schema"] ?? new JObject { ["type"] = "object", ["properties"] = new JObject() }
                    }
                }));
            }
            if ((bool)output["stream"]) output["stream_options"] = new JObject { ["include_usage"] = true };
            return output;
        }

        private static void PrepareEventStream(HttpListenerResponse response)
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.SendChunked = true;
            response.Headers["Cache-Control"] = "no-cache";
        }

        private static async Task StreamAsAnthropic(HttpListenerResponse response, HttpResponseMessage upstream, string model,
            CancellationTokenSource cancellation)
        {
            var messageId = "msg_adapter_" + Guid.NewGuid().ToString("N");
            var inputTokens = 0;
            var outputTokens = 0;
            var blocks = new Dictionary<string, int>();
            var nextIndex = 0;
            var sawTerminalSignal = false;
            var sawDoneMarker = false;
            var sawPayload = false;
            var stopReason = "end_turn";
            await WriteEvent(response, "message_start", new JObject
            {
                ["type"] = "message_start",
                ["message"] = new JObject
                {
                    ["id"] = messageId, ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                    ["content"] = new JArray(), ["stop_reason"] = JValue.CreateNull(), ["stop_sequence"] = JValue.CreateNull(),
                    ["usage"] = new JObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
                }
            }, cancellation.Token);

            using (var stream = await upstream.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string raw;
                while ((raw = await ReadLineWithHeartbeat(reader, response, cancellation)) != null)
                {
                    if (!raw.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = raw.Substring(5).Trim();
                    if (data == "[DONE]") { sawDoneMarker = true; break; }
                    JObject chunk;
                    try { chunk = JObject.Parse(data); }
                    catch { continue; }
                    if (chunk["error"] != null)
                    {
                        var message = (string)chunk["error"]?["message"] ?? (string)chunk["message"] ?? chunk["error"].ToString(Formatting.None);
                        throw new InvalidDataException("上游流式响应返回错误：" + Limit(message, 1600));
                    }
                    if (chunk["usage"] is JObject usage)
                    {
                        inputTokens = (int?)usage["prompt_tokens"] ?? inputTokens;
                        outputTokens = (int?)usage["completion_tokens"] ?? outputTokens;
                    }
                    var choices = chunk["choices"] as JArray;
                    var choice = choices == null ? null : choices.OfType<JObject>().FirstOrDefault();
                    if (choice == null) continue;
                    var finishReason = ((string)choice["finish_reason"] ?? "").Trim();
                    if (finishReason.Length > 0)
                    {
                        sawTerminalSignal = true;
                        if (finishReason.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0) stopReason = "tool_use";
                    }
                    var delta = choice["delta"] as JObject;
                    if (delta == null) continue;
                    // OpenAI-compatible reasoning models may stream private chain-of-thought in
                    // `reasoning_content` before the user-facing `content`.  Anthropic text blocks
                    // are visible output, so forwarding that field would leak internal reasoning
                    // and make the final reply look corrupted.  Only promote explicit content.
                    var text = TextFromOpenAiContent(delta["content"]);
                    if (text.Length > 0)
                    {
                        sawPayload = true;
                        if (!blocks.ContainsKey("text"))
                        {
                            blocks["text"] = nextIndex++;
                            await WriteEvent(response, "content_block_start", new JObject
                            {
                                ["type"] = "content_block_start", ["index"] = blocks["text"],
                                ["content_block"] = new JObject { ["type"] = "text", ["text"] = "" }
                            }, cancellation.Token);
                        }
                        await WriteEvent(response, "content_block_delta", new JObject
                        {
                            ["type"] = "content_block_delta", ["index"] = blocks["text"],
                            ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = text }
                        }, cancellation.Token);
                    }
                    foreach (var call in (delta["tool_calls"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        sawPayload = true;
                        stopReason = "tool_use";
                        var upstreamIndex = (string)call["index"] ?? "0";
                        var key = "tool:" + upstreamIndex;
                        var function = call["function"] as JObject ?? new JObject();
                        if (!blocks.ContainsKey(key))
                        {
                            blocks[key] = nextIndex++;
                            await WriteEvent(response, "content_block_start", new JObject
                            {
                                ["type"] = "content_block_start", ["index"] = blocks[key],
                                ["content_block"] = new JObject
                                {
                                    ["type"] = "tool_use",
                                    ["id"] = (string)call["id"] ?? "call_" + Guid.NewGuid().ToString("N").Substring(0, 20),
                                    ["name"] = (string)function["name"] ?? "tool",
                                    ["input"] = new JObject()
                                }
                            }, cancellation.Token);
                        }
                        var arguments = (string)function["arguments"] ?? "";
                        if (arguments.Length > 0)
                        {
                            await WriteEvent(response, "content_block_delta", new JObject
                            {
                                ["type"] = "content_block_delta", ["index"] = blocks[key],
                                ["delta"] = new JObject { ["type"] = "input_json_delta", ["partial_json"] = arguments }
                            }, cancellation.Token);
                        }
                    }
                }
            }
            if (!sawPayload && !sawTerminalSignal && inputTokens == 0 && outputTokens == 0)
                throw new InvalidDataException("上游流式连接已结束，但没有返回任何内容、结束标记或用量信息");
            if (!sawDoneMarker && !sawTerminalSignal)
                throw new InvalidDataException("上游流式连接在返回完成标记前意外中断，请重试或检查服务商连接");
            if (!sawPayload)
                throw new InvalidDataException("上游只返回了内部推理或空响应，没有可显示的最终内容或 Tool call");
            foreach (var index in blocks.Values.OrderBy(value => value))
                await WriteEvent(response, "content_block_stop", new JObject { ["type"] = "content_block_stop", ["index"] = index }, cancellation.Token);
            await WriteEvent(response, "message_delta", new JObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JObject { ["stop_reason"] = stopReason, ["stop_sequence"] = JValue.CreateNull() },
                ["usage"] = new JObject { ["output_tokens"] = outputTokens }
            }, cancellation.Token);
            await WriteEvent(response, "message_stop", new JObject { ["type"] = "message_stop" }, cancellation.Token);
        }

        private static async Task<string> ReadLineWithHeartbeat(StreamReader reader, HttpListenerResponse response,
            CancellationTokenSource cancellation)
        {
            var readTask = reader.ReadLineAsync();
            var started = DateTime.UtcNow;
            while (!readTask.IsCompleted)
            {
                Task completed;
                using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
                {
                    var delay = Task.Delay(HeartbeatInterval, delayCancellation.Token);
                    completed = await Task.WhenAny(readTask, delay);
                    if (completed == readTask) delayCancellation.Cancel();
                }
                if (completed == readTask) break;
                cancellation.Token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - started >= StreamIdleTimeout)
                {
                    cancellation.Cancel();
                    throw new TimeoutException("上游模型的流式响应已连续 " + Math.Ceiling(StreamIdleTimeout.TotalSeconds) + " 秒没有新数据，连接已自动取消");
                }
                await WriteEvent(response, "ping", new JObject { ["type"] = "ping" }, cancellation.Token);
            }
            return await readTask;
        }

        private static JObject NonStreamAsAnthropic(JObject data, string model)
        {
            if (data["error"] != null)
                throw new InvalidDataException("上游响应返回错误：" + Limit((string)data["error"]?["message"] ?? data["error"].ToString(Formatting.None), 1600));
            var choices = data["choices"] as JArray;
            var choice = choices == null ? null : choices.OfType<JObject>().FirstOrDefault();
            var message = choice?["message"] as JObject;
            if (message == null) throw new InvalidDataException("上游非流式响应缺少 choices[0].message");
            var content = new JArray();
            var messageText = TextFromOpenAiContent(message["content"]);
            if (!string.IsNullOrWhiteSpace(messageText)) content.Add(new JObject { ["type"] = "text", ["text"] = messageText });
            foreach (var tool in (message["tool_calls"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var function = tool["function"] as JObject ?? new JObject();
                JToken input;
                try { input = JToken.Parse((string)function["arguments"] ?? "{}"); }
                catch { input = new JObject { ["raw"] = (string)function["arguments"] ?? "" }; }
                content.Add(new JObject { ["type"] = "tool_use", ["id"] = tool["id"], ["name"] = function["name"], ["input"] = input });
            }
            if (content.Count == 0) throw new InvalidDataException("上游非流式响应没有可显示的最终内容或 Tool call");
            var usage = data["usage"] as JObject ?? new JObject();
            return new JObject
            {
                ["id"] = "msg_adapter_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                ["content"] = content, ["stop_reason"] = (message["tool_calls"] as JArray)?.Count > 0 ? "tool_use" : "end_turn", ["stop_sequence"] = JValue.CreateNull(),
                ["usage"] = new JObject { ["input_tokens"] = (int?)usage["prompt_tokens"] ?? 0, ["output_tokens"] = (int?)usage["completion_tokens"] ?? 0 }
            };
        }

        private static string TextFromBlocks(JToken blocks)
        {
            if (blocks == null) return "";
            if (blocks.Type == JTokenType.String) return (string)blocks ?? "";
            var output = new List<string>();
            foreach (var block in (blocks as JArray ?? new JArray()).OfType<JObject>())
            {
                if ((string)block["type"] == "text") output.Add((string)block["text"] ?? "");
                else if ((string)block["type"] == "image") output.Add("[输入图片]");
            }
            return string.Join("\n", output.Where(value => value.Length > 0));
        }

        private static JToken OpenAiContentFromAnthropicBlocks(JArray blocks)
        {
            var hasImages = blocks.OfType<JObject>().Any(block => string.Equals((string)block["type"], "image", StringComparison.Ordinal));
            if (!hasImages)
            {
                var text = string.Join("\n", blocks.OfType<JObject>()
                    .Where(block => string.Equals((string)block["type"], "text", StringComparison.Ordinal))
                    .Select(block => (string)block["text"] ?? "").Where(value => value.Length > 0));
                return text.Length == 0 ? null : new JValue(text);
            }

            var output = new JArray();
            foreach (var block in blocks.OfType<JObject>())
            {
                var type = (string)block["type"] ?? "";
                if (type == "text")
                {
                    var text = (string)block["text"] ?? "";
                    if (text.Length > 0) output.Add(new JObject { ["type"] = "text", ["text"] = text });
                    continue;
                }
                if (type != "image") continue;
                var source = block["source"] as JObject ?? new JObject();
                var sourceType = (string)source["type"] ?? "";
                string url;
                if (sourceType == "base64")
                {
                    var mediaType = ((string)source["media_type"] ?? "").Trim();
                    var data = ((string)source["data"] ?? "").Trim();
                    if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || data.Length == 0)
                        throw new InvalidDataException("图片内容缺少有效的 media_type 或 base64 data");
                    url = "data:" + mediaType + ";base64," + data;
                }
                else if (sourceType == "url")
                {
                    url = ((string)source["url"] ?? "").Trim();
                    Uri parsed;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                        throw new InvalidDataException("图片 URL 不是有效的 HTTP(S) 地址");
                }
                else throw new InvalidDataException("OpenAI Adapter 不支持该图片 source 类型：" + sourceType);
                output.Add(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject { ["url"] = url, ["detail"] = "auto" }
                });
            }
            return output.Count == 0 ? null : output;
        }

        private static string TextFromOpenAiContent(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return "";
            if (content.Type == JTokenType.String) return (string)content ?? "";
            if (content is JArray array)
            {
                return string.Join("", array.Select(item =>
                {
                    if (item.Type == JTokenType.String) return (string)item ?? "";
                    var value = item as JObject;
                    return (string)value?["text"] ?? (string)value?["content"] ?? "";
                }));
            }
            return "";
        }

        private static async Task WriteEvent(HttpListenerResponse response, string name, JObject data,
            CancellationToken cancellation = default(CancellationToken))
        {
            var bytes = Encoding.UTF8.GetBytes("event: " + name + "\ndata: " + data.ToString(Formatting.None) + "\n\n");
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellation);
            await response.OutputStream.FlushAsync(cancellation);
        }

        private static JObject UpstreamError(HttpStatusCode status, string raw, string token)
        {
            var code = (int)status;
            var type = code == 400 || code == 413 || code == 422 ? "invalid_request_error"
                : code == 401 ? "authentication_error"
                : code == 403 ? "permission_error"
                : code == 404 ? "not_found_error"
                : code == 429 ? "rate_limit_error"
                : code == 529 ? "overloaded_error"
                : code >= 500 ? "api_error" : "upstream_error";
            var detail = raw ?? "";
            try
            {
                var parsed = JToken.Parse(detail);
                detail = (string)parsed["error"]?["message"] ?? (string)parsed["message"] ?? parsed.ToString(Formatting.None);
            }
            catch { }
            detail = Sanitize(detail, token);
            if (string.IsNullOrWhiteSpace(detail)) detail = "上游接口未返回错误详情";
            return AdapterError(type, "上游 HTTP " + code + "：" + Limit(detail, 2000));
        }

        private static JObject AdapterError(string type, string message)
        {
            return new JObject
            {
                ["type"] = "error",
                ["error"] = new JObject { ["type"] = type, ["message"] = message ?? "请求失败" }
            };
        }

        private static string Sanitize(string value, string token)
        {
            var output = value ?? "";
            if (!string.IsNullOrWhiteSpace(token)) output = output.Replace(token, "[REDACTED_API_KEY]");
            output = Regex.Replace(output, @"(?i)bearer\s+[a-z0-9._~+/-]{8,}", "Bearer [REDACTED_API_KEY]");
            output = Regex.Replace(output, @"(?i)\bsk-[a-z0-9_-]{8,}\b", "[REDACTED_API_KEY]");
            return output;
        }

        private static bool IsClientDisconnect(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is ObjectDisposedException) return true;
                var listener = current as HttpListenerException;
                if (listener != null && (listener.ErrorCode == 64 || listener.ErrorCode == 995 || listener.ErrorCode == 1229 || listener.ErrorCode == 1236)) return true;
            }
            return false;
        }

        private static string FlattenMessage(Exception error)
        {
            var messages = new List<string>();
            for (var current = error; current != null && messages.Count < 4; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message) && !messages.Contains(current.Message)) messages.Add(current.Message.Trim());
            }
            return messages.Count == 0 ? "未知错误" : string.Join("；", messages);
        }

        private static TimeSpan ReadTimeout(string name, int fallbackSeconds)
        {
            int seconds;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out seconds) && seconds >= 1 && seconds <= 600
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(fallbackSeconds);
        }

        private static string Limit(string value, int max) { return value == null ? "" : (value.Length <= max ? value : value.Substring(0, max)); }
    }
}

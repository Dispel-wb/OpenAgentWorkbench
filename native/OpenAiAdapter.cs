using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class OpenAiAdapter
    {
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromMinutes(10) };

        public static string Endpoint(string baseUrl, string suffix)
        {
            var value = (baseUrl ?? "").TrimEnd('/');
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value;
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return value + suffix.Substring(3);
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

        public static async Task HandleAsync(HttpListenerContext context, JObject provider, string token, JObject payload)
        {
            var openAi = AnthropicToOpenAi(payload);
            var config = provider["text"] as JObject ?? new JObject();
            var url = Endpoint((string)config["baseUrl"], "/v1/chat/completions");
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                ApplyAuth(request, token, (string)provider["authStyle"] ?? "auto");
                request.Content = new StringContent(openAi.ToString(Formatting.None), Encoding.UTF8, "application/json");
                var streaming = (bool?)payload["stream"] ?? false;
                using (var upstream = await Client.SendAsync(request, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead))
                {
                    if (!upstream.IsSuccessStatusCode)
                    {
                        var error = await upstream.Content.ReadAsStringAsync();
                        await ApiServer.WriteJsonAsync(context.Response,
                            new JObject { ["type"] = "error", ["error"] = new JObject { ["type"] = "upstream_error", ["message"] = Limit(error, 2000) } },
                            (int)upstream.StatusCode);
                        return;
                    }
                    if (streaming) await StreamAsAnthropic(context.Response, upstream, (string)payload["model"] ?? "");
                    else
                    {
                        var body = JObject.Parse(await upstream.Content.ReadAsStringAsync());
                        await ApiServer.WriteJsonAsync(context.Response, NonStreamAsAnthropic(body, (string)payload["model"] ?? ""));
                    }
                }
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
                else if (textParts.Length > 0) messages.Add(new JObject { ["role"] = role, ["content"] = string.Join("\n", textParts) });

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
                output["tool_choice"] = "auto";
            }
            if ((bool)output["stream"]) output["stream_options"] = new JObject { ["include_usage"] = true };
            return output;
        }

        private static async Task StreamAsAnthropic(HttpListenerResponse response, HttpResponseMessage upstream, string model)
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.SendChunked = true;
            response.Headers["Cache-Control"] = "no-cache";
            var messageId = "msg_adapter_" + Guid.NewGuid().ToString("N");
            var inputTokens = 0;
            var outputTokens = 0;
            var blocks = new Dictionary<string, int>();
            var nextIndex = 0;
            await WriteEvent(response, "message_start", new JObject
            {
                ["type"] = "message_start",
                ["message"] = new JObject
                {
                    ["id"] = messageId, ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                    ["content"] = new JArray(), ["stop_reason"] = JValue.CreateNull(), ["stop_sequence"] = JValue.CreateNull(),
                    ["usage"] = new JObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
                }
            });

            using (var stream = await upstream.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string raw;
                while ((raw = await reader.ReadLineAsync()) != null)
                {
                    if (!raw.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = raw.Substring(5).Trim();
                    if (data == "[DONE]") break;
                    JObject chunk;
                    try { chunk = JObject.Parse(data); }
                    catch { continue; }
                    if (chunk["usage"] is JObject usage)
                    {
                        inputTokens = (int?)usage["prompt_tokens"] ?? inputTokens;
                        outputTokens = (int?)usage["completion_tokens"] ?? outputTokens;
                    }
                    var delta = chunk["choices"]?[0]?["delta"] as JObject;
                    if (delta == null) continue;
                    var text = (string)delta["content"] ?? "";
                    if (text.Length > 0)
                    {
                        if (!blocks.ContainsKey("text"))
                        {
                            blocks["text"] = nextIndex++;
                            await WriteEvent(response, "content_block_start", new JObject
                            {
                                ["type"] = "content_block_start", ["index"] = blocks["text"],
                                ["content_block"] = new JObject { ["type"] = "text", ["text"] = "" }
                            });
                        }
                        await WriteEvent(response, "content_block_delta", new JObject
                        {
                            ["type"] = "content_block_delta", ["index"] = blocks["text"],
                            ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = text }
                        });
                    }
                    foreach (var call in (delta["tool_calls"] as JArray ?? new JArray()).OfType<JObject>())
                    {
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
                            });
                        }
                        var arguments = (string)function["arguments"] ?? "";
                        if (arguments.Length > 0)
                        {
                            await WriteEvent(response, "content_block_delta", new JObject
                            {
                                ["type"] = "content_block_delta", ["index"] = blocks[key],
                                ["delta"] = new JObject { ["type"] = "input_json_delta", ["partial_json"] = arguments }
                            });
                        }
                    }
                }
            }
            foreach (var index in blocks.Values.OrderBy(value => value))
                await WriteEvent(response, "content_block_stop", new JObject { ["type"] = "content_block_stop", ["index"] = index });
            await WriteEvent(response, "message_delta", new JObject
            {
                ["type"] = "message_delta",
                ["delta"] = new JObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = JValue.CreateNull() },
                ["usage"] = new JObject { ["output_tokens"] = outputTokens }
            });
            await WriteEvent(response, "message_stop", new JObject { ["type"] = "message_stop" });
            response.OutputStream.Close();
        }

        private static JObject NonStreamAsAnthropic(JObject data, string model)
        {
            var message = data["choices"]?[0]?["message"] as JObject ?? new JObject();
            var content = new JArray();
            if (!string.IsNullOrWhiteSpace((string)message["content"])) content.Add(new JObject { ["type"] = "text", ["text"] = message["content"] });
            foreach (var tool in (message["tool_calls"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var function = tool["function"] as JObject ?? new JObject();
                JToken input;
                try { input = JToken.Parse((string)function["arguments"] ?? "{}"); }
                catch { input = new JObject { ["raw"] = (string)function["arguments"] ?? "" }; }
                content.Add(new JObject { ["type"] = "tool_use", ["id"] = tool["id"], ["name"] = function["name"], ["input"] = input });
            }
            var usage = data["usage"] as JObject ?? new JObject();
            return new JObject
            {
                ["id"] = "msg_adapter_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                ["content"] = content, ["stop_reason"] = message["tool_calls"] != null ? "tool_use" : "end_turn", ["stop_sequence"] = JValue.CreateNull(),
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

        private static async Task WriteEvent(HttpListenerResponse response, string name, JObject data)
        {
            var bytes = Encoding.UTF8.GetBytes("event: " + name + "\ndata: " + data.ToString(Formatting.None) + "\n\n");
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            await response.OutputStream.FlushAsync();
        }

        private static string Limit(string value, int max) { return value == null ? "" : (value.Length <= max ? value : value.Substring(0, max)); }
    }
}

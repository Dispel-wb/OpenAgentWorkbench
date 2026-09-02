using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

internal static class FakeClaudeWorker
{
    private static int Main(string[] args)
    {
        if (args != null && Array.Exists(args, value => value == "--version")) { Console.WriteLine("fake-claude 1.0.0"); return 0; }
        if (args != null && args.Length > 0 && (args[0] == "plugin" || args[0] == "mcp")) { Console.WriteLine("fake-claude management command ok"); return 0; }
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        using (var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true)))
        using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
        {
            string line;
            while ((line = input.ReadLine()) != null)
            {
                var value = JObject.Parse(line);
                var text = (string)value["message"]?["content"]?[0]?["text"] ?? "";
                var authCapture = Environment.GetEnvironmentVariable("CLAUDE_GUI_AUTH_CAPTURE") ?? "";
                if (authCapture.Length > 0)
                {
                    File.AppendAllText(authCapture, new JObject
                    {
                        ["prompt"] = text,
                        ["apiKey"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "",
                        ["authToken"] = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") ?? "",
                        ["baseUrl"] = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") ?? ""
                    }.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
                }
                if (text.StartsWith("stream-stress:", StringComparison.OrdinalIgnoreCase))
                {
                    int requested;
                    if (!int.TryParse(text.Substring("stream-stress:".Length), out requested)) requested = 3000;
                    requested = Math.Max(1, Math.Min(10000, requested));
                    for (var index = 0; index < requested; index++)
                    {
                        output.WriteLine(new JObject
                        {
                            ["type"] = "stream_event",
                            ["event"] = new JObject
                            {
                                ["type"] = "content_block_delta",
                                ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = "chunk-" + index + " " }
                            }
                        }.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    output.WriteLine(new JObject
                    {
                        ["type"] = "result", ["is_error"] = false, ["result"] = "stream-stress-complete",
                        ["usage"] = new JObject { ["input_tokens"] = text.Length, ["output_tokens"] = requested * 3 }
                    }.ToString(Newtonsoft.Json.Formatting.None));
                    continue;
                }
                if (text.Contains("tool-runtime-timeout") || text.Contains("tool-runtime-cancel"))
                {
                    output.WriteLine(new JObject
                    {
                        ["type"] = "assistant",
                        ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject
                        {
                            ["type"] = "tool_use", ["id"] = "fixture-tool-" + Guid.NewGuid().ToString("N"), ["name"] = "Bash",
                            ["input"] = new JObject { ["command"] = "fixture", ["api_key"] = "sk-fixture-tool-runtime-secret" }
                        }) }
                    }.ToString(Newtonsoft.Json.Formatting.None));
                    System.Threading.Thread.Sleep(10000);
                    continue;
                }
                if (text.Contains("中断恢复")) System.Threading.Thread.Sleep(1200);
                if (text.Contains("run-center-slow")) System.Threading.Thread.Sleep(15000);
                if (text.Contains("fault-recovery")) System.Threading.Thread.Sleep(300);
                if (text.Contains("provider-rate-limit"))
                {
                    output.WriteLine(new JObject
                    {
                        ["type"] = "result", ["is_error"] = true,
                        ["result"] = "429 rate limit exceeded; retry later"
                    }.ToString(Newtonsoft.Json.Formatting.None));
                    continue;
                }
                if (text.Contains("provider-auth-failure"))
                {
                    output.WriteLine(new JObject
                    {
                        ["type"] = "result", ["is_error"] = true,
                        ["result"] = "401 invalid API key sk-provider-health-secret-value"
                    }.ToString(Newtonsoft.Json.Formatting.None));
                    continue;
                }
                if (text.Contains("compaction-event"))
                {
                    output.WriteLine(new JObject
                    {
                        ["type"] = "system", ["subtype"] = "compact_boundary", ["trigger"] = "automatic",
                        ["pre_tokens"] = 96000, ["post_tokens"] = 18000, ["uuid"] = "fixture-compaction-boundary"
                    }.ToString(Newtonsoft.Json.Formatting.None));
                }
                if (text.Contains("m2-write"))
                {
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "agent-change.txt"), "isolated-change", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "base.txt"), "isolated-base-change", new UTF8Encoding(false));
                }
                output.WriteLine(new JObject
                {
                    ["type"] = "assistant",
                    ["message"] = new JObject { ["role"] = "assistant", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) }
                }.ToString(Newtonsoft.Json.Formatting.None));
                output.WriteLine(new JObject
                {
                    ["type"] = "result", ["is_error"] = false, ["result"] = text,
                    ["usage"] = new JObject { ["input_tokens"] = text.Length, ["output_tokens"] = text.Length }
                }.ToString(Newtonsoft.Json.Formatting.None));
            }
        }
        return 0;
    }
}

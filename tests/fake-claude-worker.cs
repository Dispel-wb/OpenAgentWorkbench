using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

internal static class FakeClaudeWorker
{
    private static int Main()
    {
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
                if (text.Contains("中断恢复")) System.Threading.Thread.Sleep(1200);
                if (text.Contains("fault-recovery")) System.Threading.Thread.Sleep(300);
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

using System;

internal static class FakeCodexWorker
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        if (Array.IndexOf(args, "--version") >= 0) { Console.WriteLine("codex-cli fixture-1.0"); return 0; }
        var prompt = Console.In.ReadToEnd();
        if (prompt.Contains("fixture-oversize")) { Console.Write(new string('x', 8 * 1024 * 1024 + 1)); Console.Out.Flush(); System.Threading.Thread.Sleep(60000); return 0; }
        if (prompt.Contains("fixture-malformed")) Console.WriteLine("{invalid JSON}");
        if (prompt.Contains("fixture-require-memory") && (!prompt.Contains("fixture-core-memory") || prompt.Contains("fixture-inactive-memory"))) return 7;
        Console.WriteLine("{\"type\":\"thread.started\",\"thread_id\":\"11111111-1111-4111-8111-111111111111\"}");
        Console.WriteLine("{\"type\":\"turn.started\"}");
        if (prompt.Contains("fixture-terminal-failure")) { Console.WriteLine("{\"type\":\"turn.failed\",\"error\":{\"message\":\"fixture failure despite exit zero\"}}"); return 0; }
        if (prompt.Contains("fixture-no-terminal")) return 0;
        if (prompt.Contains("fixture-answer-limit"))
            for (var i = 0; i < 9; i++) Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"" + new string('x', 1024 * 1024) + "\"}}");
        Console.WriteLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Codex 中文回复\"}}");
        Console.WriteLine(Array.IndexOf(args, "resume") >= 0 ? "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":6,\"output_tokens\":8,\"total_tokens\":14}}" : "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":3,\"output_tokens\":4,\"total_tokens\":7}}");
        if (prompt.Contains("fixture-hang-after-terminal")) { Console.Out.Flush(); System.Threading.Thread.Sleep(60000); }
        return prompt.Contains("中文") ? 0 : 2;
    }
}

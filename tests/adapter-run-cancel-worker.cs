using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

internal static class AdapterRunCancelWorker
{
    private static int Main(string[] args)
    {
        if (args != null && Array.Exists(args, value => value == "--version"))
        {
            Console.WriteLine("adapter-run-cancel-worker 1.0.0");
            return 0;
        }
        Console.InputEncoding = new UTF8Encoding(false);
        var input = Console.ReadLine();
        if (input == null) return 0;
        var baseUrl = (Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") ?? "").TrimEnd('/');
        var token = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") ?? "";
        var model = input.IndexOf("adapter-run-stream-error", StringComparison.OrdinalIgnoreCase) >= 0 ? "stream-error-model"
            : input.IndexOf("adapter-run-error", StringComparison.OrdinalIgnoreCase) >= 0 ? "bad-model" : "hang-model";
        using (var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan })
        using (var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/messages"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{\"model\":\"" + model + "\",\"stream\":true,\"max_tokens\":20,\"messages\":[{\"role\":\"user\",\"content\":\"run test\"}]}", Encoding.UTF8, "application/json");
            using (var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            {
                var buffer = new byte[1024];
                while (stream.Read(buffer, 0, buffer.Length) > 0) { }
            }
        }
        return 0;
    }
}

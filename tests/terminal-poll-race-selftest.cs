using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

internal static class TerminalPollRaceSelfTest
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(args[0]);
            var apiType = assembly.GetType("ClaudeCodeWorkbench.ApiServer", true);
            var jobType = apiType.GetNestedType("Job", BindingFlags.NonPublic);
            foreach (var kind in new[] { "local_runtime", "authentication" })
                Check(apiType, jobType, Path.Combine(args[1], kind), kind);
            Console.WriteLine("Terminal poll race PASS: 2 controlled background-finalization interleavings");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(Type apiType, Type jobType, string root, string kind)
    {
        Directory.CreateDirectory(root);
        var statusPath = Path.Combine(root, "status.json");
        File.WriteAllText(statusPath, "{\"state\":\"failed\",\"message\":\"fixture failure\"}", new UTF8Encoding(false));
        var job = jobType.GetMethod("Chat", BindingFlags.Static | BindingFlags.Public).Invoke(null, new object[] { kind, root, null });
        var gate = jobType.GetField("StateGate", Fields).GetValue(job);
        var api = FormatterServices.GetUninitializedObject(apiType);
        var jobsField = apiType.GetField("_jobs", Fields);
        var jobs = Activator.CreateInstance(jobsField.FieldType);
        jobs.GetType().GetMethod("TryAdd").Invoke(jobs, new[] { (object)kind, job });
        jobsField.SetValue(api, jobs);
        var poll = apiType.GetMethod("PollChat", BindingFlags.Instance | BindingFlags.NonPublic);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using (var listener = new HttpListener())
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) })
        {
            var address = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(address);
            listener.Start();
            var responseTask = client.GetAsync(address + "?after=0");
            var context = listener.GetContext();
            Exception pollError = null;
            Thread thread = null;
            Monitor.Enter(gate);
            try
            {
                thread = new Thread(() =>
                {
                    try { ((Task)poll.Invoke(api, new object[] { context, kind })).GetAwaiter().GetResult(); }
                    catch (Exception error) { pollError = error; }
                    finally { context.Response.Close(); }
                }) { IsBackground = true };
                thread.Start();
                // Poll has read the raw terminal status and is now blocked on the finalization gate.
                // Nothing else in this fixture holds a lock used by PollChat.
                if (!SpinWait.SpinUntil(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000))
                    throw new Exception("Poll did not reach the finalization gate");
                var durable = new JObject { ["state"] = "failed", ["message"] = "fixture failure", ["failureClassification"] = new JObject { ["kind"] = kind } };
                if (kind == "local_runtime") durable["providerHealthSkipped"] = "local_runtime";
                else
                {
                    durable["providerHealthRecorded"] = true;
                    durable["fallbackDecision"] = new JObject { ["automatic"] = false, ["candidateCount"] = 1 };
                }
                File.WriteAllText(statusPath, durable.ToString(), new UTF8Encoding(false));
                jobType.GetField("IsActive", Fields).SetValue(job, false);
                jobType.GetField("Busy", Fields).SetValue(job, false);
                jobType.GetField("State", Fields).SetValue(job, "failed");
            }
            finally { Monitor.Exit(gate); }
            if (!thread.Join(5000)) throw new Exception("Poll did not finish after releasing its gate");
            if (pollError != null) throw new Exception("Poll failed", pollError);
            var response = responseTask.GetAwaiter().GetResult();
            var payload = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if ((string)payload["status"]?["failureClassification"]?["kind"] != kind)
                throw new Exception("Poll returned stale terminal metadata after background finalization: " + payload["status"]);
            if (kind == "authentication" && payload["status"]?["fallbackDecision"] == null)
                throw new Exception("Poll dropped the persisted fallback decision");
            if (kind == "local_runtime" && (string)payload["status"]?["providerHealthSkipped"] != "local_runtime")
                throw new Exception("Poll dropped the local-runtime classification");
        }
    }
}

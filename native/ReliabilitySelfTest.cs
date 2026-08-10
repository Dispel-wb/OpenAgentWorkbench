using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class ReliabilitySelfTest
    {
        public static JObject Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-workbench-selftest-" + Guid.NewGuid().ToString("N"));
            var checks = new JArray();
            Directory.CreateDirectory(root);
            try
            {
                var statePath = Path.Combine(root, "job-state.json");
                var state = new PersistedJobState(statePath, "job-a", "chat");
                state.SetIdentity("session-a", root, "model-a");
                state.SetRequestId("schedule:test:one");
                state.Transition(JobStates.Starting, "start");
                state.BeginTurn(0, "running");
                Add(checks, "job-state-running", state.State == JobStates.Running);

                var restored = new PersistedJobState(statePath, "job-a", "chat");
                Add(checks, "job-state-restart", restored.State == JobStates.Running && (string)restored.Snapshot["sessionId"] == "session-a" && (string)restored.Snapshot["requestId"] == "schedule:test:one");
                restored.Transition(JobStates.Completed, "done");
                Add(checks, "job-state-terminal", restored.State == JobStates.Completed);

                var eventPath = Path.Combine(root, "stream.jsonl");
                File.WriteAllLines(eventPath, new[] { "{\"type\":\"a\"}", "{\"type\":\"b\"}", "{\"type\":\"c\"}" }, new UTF8Encoding(false));
                var events = new JobEventStore("job-a", eventPath);
                long next;
                var first = events.ReadAfter(0, 100, out next);
                Add(checks, "event-monotonic-sequence", next == 3 && first.Select(value => (long)value["seq"]).SequenceEqual(new long[] { 1, 2, 3 }));
                long replayNext;
                var replay = events.ReadAfter(1, 100, out replayNext);
                Add(checks, "event-breakpoint-replay", replayNext == 3 && replay.Count == 2 && (long)replay[0]["seq"] == 2);
                var ids = new HashSet<string>(first.Select(value => (string)value["id"]));
                foreach (var item in replay) ids.Add((string)item["id"]);
                Add(checks, "event-idempotent-dedupe", ids.Count == 3);
                File.AppendAllText(eventPath, "{\"type\":\"partial\"}", new UTF8Encoding(false));
                Add(checks, "event-ignore-partial-write", events.Count() == 3);
                File.AppendAllText(eventPath, Environment.NewLine, new UTF8Encoding(false));
                Add(checks, "event-index-incremental", events.Count() == 4);

                Add(checks, "zip-normal-path", WorkbenchApi.SafeZipRelative("skin/assets/mascot.png").EndsWith("mascot.png", StringComparison.Ordinal));
                Add(checks, "zip-reject-parent", RejectZip("../outside.txt"));
                Add(checks, "zip-reject-root", RejectZip("/outside.txt"));
                Add(checks, "zip-reject-drive", RejectZip("C:/outside.txt"));

                var atomic = JObject.Parse(File.ReadAllText(statePath, Encoding.UTF8));
                Add(checks, "atomic-state-json", (string)atomic["state"] == JobStates.Completed);
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }
            return new JObject
            {
                ["ok"] = checks.OfType<JObject>().All(value => (bool)value["passed"]),
                ["checks"] = checks,
                ["checkedAt"] = ProviderStore.NowIso()
            };
        }

        private static bool RejectZip(string value)
        {
            try { WorkbenchApi.SafeZipRelative(value); return false; }
            catch (InvalidOperationException) { return true; }
        }

        private static void Add(JArray checks, string name, bool passed)
        {
            checks.Add(new JObject { ["name"] = name, ["passed"] = passed });
        }
    }
}

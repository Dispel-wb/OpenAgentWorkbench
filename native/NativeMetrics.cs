using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    // Local-only bounded observability. It never receives request bodies or credentials.
    internal static class NativeMetrics
    {
        private const int MaxSamples = 120;
        private const int MaxRoutes = 80;
        private static readonly object Gate = new object();
        private static JObject _data;
        private static Timer _saveTimer;
        private static bool _dirty;
        private static DateTime _lastSaveUtc = DateTime.MinValue;
        private static long _sessionCrashBaseline;
        private static long _sessionWorkerErrorBaseline;
        private static long _sessionHandledErrorBaseline;
        private static long _sessionHttpTotalBaseline;
        private static long _sessionHttpErrorBaseline;
        private static long _sessionRunStartedBaseline;
        private static long _sessionRunCompletedBaseline;
        private static long _sessionRunFailedBaseline;
        private static long _sessionRunCancelledBaseline;
        private static readonly List<long> SessionHttpDurations = new List<long>();
        private static readonly List<long> SessionRunDurations = new List<long>();
        private static readonly HashSet<string> UserWaitRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/workbench/activity",
            "/api/files/select",
            "/api/files/export-text",
            "/api/workbench/projects/add",
            "/api/workbench/runtime/claude/configure",
            "/api/workbench/skins/import",
            "/api/workbench/extensions/import"
        };
        private static string _sessionStartedAt = "";

        public sealed class RequestScope
        {
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly string Method;
            internal readonly string Route;
            internal RequestScope(string method, string route) { Method = method; Route = route; }
        }

        public static RequestScope BeginRequest(string method, string path)
        {
            return new RequestScope((method ?? "GET").ToUpperInvariant(), NormalizeRoute(path));
        }

        public static void EndRequest(RequestScope scope, int status, Exception error)
        {
            if (scope == null) return;
            try
            {
                var elapsed = Math.Max(0L, scope.Clock.ElapsedMilliseconds);
                lock (Gate)
                {
                    var data = Load();
                    var http = Object(data, "http");
                    http["total"] = Number(http, "total") + 1;
                    if (status >= 400 || error != null) http["errors"] = Number(http, "errors") + 1;
                    var statuses = Object(http, "byStatus");
                    var statusKey = status <= 0 ? "0" : status.ToString();
                    statuses[statusKey] = Number(statuses, statusKey) + 1;
                    var routes = Object(http, "byRoute");
                    var route = Limit(scope.Method + " " + scope.Route, 120);
                    if (routes[route] == null && routes.Properties().Count() >= MaxRoutes) route = "[other]";
                    routes[route] = Number(routes, route) + 1;
                    var measureLatency = !UserWaitRoutes.Contains(scope.Route);
                    if (measureLatency)
                    {
                        var durations = Object(http, "durations");
                        durations["count"] = Number(durations, "count") + 1;
                        durations["sumMs"] = Number(durations, "sumMs") + elapsed;
                        durations["maxMs"] = Math.Max(Number(durations, "maxMs"), elapsed);
                        AddNumberSample(durations, "samplesMs", elapsed);
                        AddSessionSample(SessionHttpDurations, elapsed);
                        if (elapsed >= 1500 || status >= 500 || error != null)
                        {
                            var samples = Array(http, "slowRequests");
                            samples.Add(new JObject { ["at"] = ProviderStore.NowIso(), ["method"] = scope.Method,
                                ["route"] = scope.Route, ["status"] = status, ["durationMs"] = elapsed,
                                ["error"] = error == null ? null : Limit(SecretRedactor.Redact(error.Message), 240) });
                            Trim(samples);
                        }
                    }
                    Touch(data);
                    QueueSave(data, false);
                }
            }
            catch { }
        }

        public static void RecordHostStart(bool restart)
        {
            Update(delegate(JObject data)
            {
                var host = Object(data, "host");
                _sessionCrashBaseline = Number(host, "crashes");
                _sessionWorkerErrorBaseline = Number(host, "workerErrors");
                _sessionHandledErrorBaseline = Number(host, "handledErrors");
                var http = Object(data, "http");
                _sessionHttpTotalBaseline = Number(http, "total");
                _sessionHttpErrorBaseline = Number(http, "errors");
                var runs = Object(data, "runs");
                _sessionRunStartedBaseline = Number(runs, "started");
                _sessionRunCompletedBaseline = Number(runs, "completed");
                _sessionRunFailedBaseline = Number(runs, "failed");
                _sessionRunCancelledBaseline = Number(runs, "cancelled");
                SessionHttpDurations.Clear();
                SessionRunDurations.Clear();
                _sessionStartedAt = ProviderStore.NowIso();
                host["starts"] = Number(host, "starts") + 1;
                if (restart) host["restarts"] = Number(host, "restarts") + 1;
            }, true);
        }

        public static void RecordUiReconnect() { Increment("host", "uiReconnects"); }
        public static void RecordWorkerError(string stage)
        {
            Update(delegate(JObject data)
            {
                var host = Object(data, "host");
                host["workerErrors"] = Number(host, "workerErrors") + 1;
                host["lastWorkerError"] = Limit(SecretRedactor.Redact(stage), 160);
            }, true);
        }

        public static void RecordCrash(string stage, Exception error)
        {
            Update(delegate(JObject data)
            {
                var host = Object(data, "host");
                host["crashes"] = Number(host, "crashes") + 1;
                host["lastCrash"] = new JObject { ["at"] = ProviderStore.NowIso(), ["stage"] = Limit(SecretRedactor.Redact(stage), 160),
                    ["message"] = Limit(SecretRedactor.Redact(error == null ? "unknown" : error.Message), 240) };
            }, true);
        }

        public static void RecordHandledError(string stage, Exception error)
        {
            Update(delegate(JObject data)
            {
                var host = Object(data, "host");
                host["handledErrors"] = Number(host, "handledErrors") + 1;
                host["lastHandledError"] = new JObject { ["at"] = ProviderStore.NowIso(), ["stage"] = Limit(SecretRedactor.Redact(stage), 160),
                    ["message"] = Limit(SecretRedactor.Redact(error == null ? "unknown" : error.Message), 240) };
            }, true);
        }

        public static void RecordRunStart(string runId)
        {
            Update(delegate(JObject data)
            {
                var runs = Object(data, "runs");
                var activeIds = Array(runs, "activeRunIds");
                if (activeIds.Values<string>().Any(value => string.Equals(value, runId, StringComparison.OrdinalIgnoreCase))) return;
                runs["started"] = Number(runs, "started") + 1;
                activeIds.Add(runId ?? "");
                Trim(activeIds, 256);
                runs["active"] = activeIds.Count;
            });
        }

        public static void RecordRunEnd(string runId, string state, long durationMs)
        {
            Update(delegate(JObject data)
            {
                var runs = Object(data, "runs");
                var endedIds = Array(runs, "recentEndedRunIds");
                if (endedIds.Values<string>().Any(value => string.Equals(value, runId, StringComparison.OrdinalIgnoreCase))) return;
                var activeIds = Array(runs, "activeRunIds");
                foreach (var token in activeIds.Where(token => string.Equals((string)token, runId, StringComparison.OrdinalIgnoreCase)).ToArray()) token.Remove();
                endedIds.Add(runId ?? "");
                Trim(endedIds, 256);
                var key = string.Equals(state, JobStates.Completed, StringComparison.OrdinalIgnoreCase) ? "completed" :
                    string.Equals(state, JobStates.Cancelled, StringComparison.OrdinalIgnoreCase) ? "cancelled" : "failed";
                runs[key] = Number(runs, key) + 1;
                runs["active"] = activeIds.Count;
                runs["durationSumMs"] = Number(runs, "durationSumMs") + Math.Max(0L, durationMs);
                AddNumberSample(runs, "durationsMs", Math.Max(0L, durationMs));
                AddSessionSample(SessionRunDurations, Math.Max(0L, durationMs));
                if (durationMs >= 5000 || key == "failed")
                {
                    var samples = Array(runs, "slowRuns");
                    samples.Add(new JObject { ["at"] = ProviderStore.NowIso(), ["state"] = key, ["durationMs"] = Math.Max(0L, durationMs) });
                    Trim(samples);
                }
            }, true);
        }

        public static void ReconcileActiveRuns(IEnumerable<string> runIds)
        {
            Update(delegate(JObject data)
            {
                var runs = Object(data, "runs");
                var activeIds = Array(runs, "activeRunIds");
                activeIds.RemoveAll();
                foreach (var id in (runIds ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(256)) activeIds.Add(id);
                runs["active"] = activeIds.Count;
            }, true);
        }

        public static JObject Snapshot()
        {
            lock (Gate)
            {
                var data = Load();
                var copy = (JObject)data.DeepClone();
                AddSummary(copy);
                return copy;
            }
        }

        public static void Flush()
        {
            try
            {
                lock (Gate)
                {
                    if (_data == null || !_dirty) return;
                    Save(_data); _dirty = false; _lastSaveUtc = DateTime.UtcNow;
                    if (_saveTimer != null) { _saveTimer.Dispose(); _saveTimer = null; }
                }
            }
            catch { }
        }

        private static void AddSummary(JObject data)
        {
            var host = Object(data, "host");
            host["currentSession"] = new JObject
            {
                ["startedAt"] = _sessionStartedAt,
                ["crashes"] = Math.Max(0L, Number(host, "crashes") - _sessionCrashBaseline),
                ["workerErrors"] = Math.Max(0L, Number(host, "workerErrors") - _sessionWorkerErrorBaseline),
                ["handledErrors"] = Math.Max(0L, Number(host, "handledErrors") - _sessionHandledErrorBaseline)
            };
            var http = Object(data, "http"); var durations = Object(http, "durations");
            var samples = Array(durations, "samplesMs").Values<long>().OrderBy(value => value).ToArray();
            durations["p50Ms"] = Percentile(samples, 0.50); durations["p95Ms"] = Percentile(samples, 0.95);
            var sessionHttp = SessionHttpDurations.OrderBy(value => value).ToArray();
            http["currentSession"] = new JObject
            {
                ["startedAt"] = _sessionStartedAt,
                ["total"] = Math.Max(0L, Number(http, "total") - _sessionHttpTotalBaseline),
                ["errors"] = Math.Max(0L, Number(http, "errors") - _sessionHttpErrorBaseline),
                ["p50Ms"] = Percentile(sessionHttp, 0.50),
                ["p95Ms"] = Percentile(sessionHttp, 0.95),
                ["maxMs"] = sessionHttp.Length == 0 ? 0L : sessionHttp.Max()
            };
            var runs = Object(data, "runs"); var runSamples = Array(runs, "durationsMs").Values<long>().OrderBy(value => value).ToArray();
            runs["p50Ms"] = Percentile(runSamples, 0.50); runs["p95Ms"] = Percentile(runSamples, 0.95);
            var sessionRuns = SessionRunDurations.OrderBy(value => value).ToArray();
            runs["currentSession"] = new JObject
            {
                ["startedAt"] = _sessionStartedAt,
                ["started"] = Math.Max(0L, Number(runs, "started") - _sessionRunStartedBaseline),
                ["active"] = Number(runs, "active"),
                ["completed"] = Math.Max(0L, Number(runs, "completed") - _sessionRunCompletedBaseline),
                ["failed"] = Math.Max(0L, Number(runs, "failed") - _sessionRunFailedBaseline),
                ["cancelled"] = Math.Max(0L, Number(runs, "cancelled") - _sessionRunCancelledBaseline),
                ["p50Ms"] = Percentile(sessionRuns, 0.50),
                ["p95Ms"] = Percentile(sessionRuns, 0.95),
                ["maxMs"] = sessionRuns.Length == 0 ? 0L : sessionRuns.Max()
            };
        }

        private static void Increment(string group, string key) { Update(data => { var item = Object(data, group); item[key] = Number(item, key) + 1; }); }
        private static void Update(Action<JObject> action, bool urgent = false)
        {
            try { lock (Gate) { var data = Load(); action(data); Touch(data); QueueSave(data, urgent); } } catch { }
        }

        private static void QueueSave(JObject data, bool urgent)
        {
            _dirty = true;
            if (urgent || _lastSaveUtc == DateTime.MinValue || (DateTime.UtcNow - _lastSaveUtc).TotalSeconds >= 2)
            {
                Save(data); _dirty = false; _lastSaveUtc = DateTime.UtcNow;
                if (_saveTimer != null) { _saveTimer.Dispose(); _saveTimer = null; }
                return;
            }
            if (_saveTimer != null) return;
            _saveTimer = new Timer(delegate
            {
                lock (Gate)
                {
                    try { if (_dirty && _data != null) { Save(_data); _dirty = false; _lastSaveUtc = DateTime.UtcNow; } } catch { }
                    try { if (_saveTimer != null) _saveTimer.Dispose(); } catch { }
                    _saveTimer = null;
                }
            }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }

        private static JObject Load()
        {
            if (_data != null) { EnsureShape(_data); return _data; }
            var path = FilePath();
            try { _data = File.Exists(path) ? JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) as JObject : null; } catch { _data = null; }
            if (_data == null) _data = new JObject { ["schemaVersion"] = 2, ["startedAt"] = ProviderStore.NowIso(), ["host"] = new JObject(), ["http"] = new JObject(), ["runs"] = new JObject() };
            EnsureShape(_data);
            return _data;
        }

        private static void EnsureShape(JObject data)
        {
            var host = Object(data, "host");
            foreach (var key in new[] { "starts", "restarts", "uiReconnects", "workerErrors", "handledErrors", "crashes" }) if (host[key] == null) host[key] = 0L;
            var http = Object(data, "http");
            foreach (var key in new[] { "total", "errors" }) if (http[key] == null) http[key] = 0L;
            Object(http, "byStatus"); Object(http, "byRoute"); Array(http, "slowRequests");
            var durations = Object(http, "durations");
            foreach (var key in new[] { "count", "sumMs", "maxMs", "p50Ms", "p95Ms" }) if (durations[key] == null) durations[key] = 0L;
            Array(durations, "samplesMs");
            var runs = Object(data, "runs");
            foreach (var key in new[] { "started", "active", "completed", "failed", "cancelled", "durationSumMs", "p50Ms", "p95Ms" }) if (runs[key] == null) runs[key] = 0L;
            Array(runs, "durationsMs"); Array(runs, "slowRuns"); Array(runs, "activeRunIds"); Array(runs, "recentEndedRunIds");
            data["schemaVersion"] = 2;
        }

        private static void Save(JObject data)
        {
            var path = FilePath(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp"; File.WriteAllText(temp, data.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        private static string FilePath()
        {
            var root = AppPaths.Data;
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EditionInfo.StorageId);
            return Path.Combine(root, "native-metrics.json");
        }
        private static JObject Object(JObject parent, string key) { var value = parent[key] as JObject; if (value == null) { value = new JObject(); parent[key] = value; } return value; }
        private static JArray Array(JObject parent, string key) { var value = parent[key] as JArray; if (value == null) { value = new JArray(); parent[key] = value; } return value; }
        private static long Number(JObject parent, string key) { return (long?)parent[key] ?? 0L; }
        private static void AddNumberSample(JObject parent, string key, long value) { var values = Array(parent, key); values.Add(value); Trim(values); }
        private static void AddSessionSample(List<long> values, long value) { values.Add(value); while (values.Count > MaxSamples) values.RemoveAt(0); }
        private static void Trim(JArray values) { Trim(values, MaxSamples); }
        private static void Trim(JArray values, int limit) { while (values.Count > limit) values.RemoveAt(0); }
        private static long Percentile(long[] values, double fraction) { if (values.Length == 0) return 0; var index = (int)Math.Round((values.Length - 1) * fraction); return values[Math.Max(0, Math.Min(values.Length - 1, index))]; }
        private static void Touch(JObject data) { data["updatedAt"] = ProviderStore.NowIso(); }
        private static string NormalizeRoute(string path)
        {
            var value = path ?? "/"; var query = value.IndexOf('?'); if (query >= 0) value = value.Substring(0, query);
            if (value.Length > 120) value = value.Substring(0, 120);
            return value.StartsWith("/", StringComparison.Ordinal) ? value : "/";
        }
        private static string Limit(string value, int length) { value = value ?? ""; return value.Length <= length ? value : value.Substring(0, length); }
    }
}

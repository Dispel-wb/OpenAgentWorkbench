using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal static class JobStates
    {
        public const string Queued = "queued";
        public const string Starting = "starting";
        public const string Running = "running";
        public const string Waiting = "waiting";
        public const string Paused = "paused";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public static bool CanTransition(string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) || from == to) return true;
            if (to == Cancelled || to == Failed) return from != Cancelled;
            if (from == Queued) return to == Starting || to == Running;
            if (from == Starting) return to == Running || to == Waiting;
            if (from == Running) return to == Waiting || to == Paused || to == Completed;
            if (from == Waiting) return to == Running || to == Paused || to == Completed;
            if (from == Paused) return to == Running || to == Completed;
            if (from == Completed || from == Failed) return to == Running || to == Starting;
            return false;
        }
    }

    internal sealed class PersistedJobState
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private JObject _value;

        public PersistedJobState(string path, string id, string kind)
        {
            _path = path;
            _value = JsonUtil.Read(path, new JObject()) as JObject ?? new JObject();
            if (_value.Count == 0)
            {
                _value = new JObject
                {
                    ["schemaVersion"] = 1, ["id"] = id, ["kind"] = kind,
                    ["state"] = JobStates.Queued, ["turnStartSeq"] = 0L,
                    ["createdAt"] = ProviderStore.NowIso(), ["updatedAt"] = ProviderStore.NowIso()
                };
                Save();
            }
        }

        public JObject Snapshot { get { lock (_gate) return (JObject)_value.DeepClone(); } }
        public string State { get { lock (_gate) return (string)_value["state"] ?? JobStates.Queued; } }
        public long TurnStartSeq { get { lock (_gate) return (long?)_value["turnStartSeq"] ?? 0L; } }

        public void SetIdentity(string sessionId, string workspace, string model)
        {
            lock (_gate)
            {
                _value["sessionId"] = sessionId ?? ""; _value["workspace"] = workspace ?? ""; _value["model"] = model ?? "";
                _value["updatedAt"] = ProviderStore.NowIso(); Save();
            }
        }

        public void SetRequestId(string requestId)
        {
            lock (_gate)
            {
                _value["requestId"] = requestId ?? "";
                _value["updatedAt"] = ProviderStore.NowIso(); Save();
            }
        }

        public void BeginTurn(long startSeq, string message)
        {
            lock (_gate)
            {
                _value["turnStartSeq"] = Math.Max(0L, startSeq);
                TransitionLocked(JobStates.Running, message, null);
            }
        }

        public void Transition(string next, string message, JObject details = null)
        {
            lock (_gate) TransitionLocked(next, message, details);
        }

        public void ReconcileTerminal(string terminal, string message, JObject details = null)
        {
            if (terminal != JobStates.Completed && terminal != JobStates.Failed && terminal != JobStates.Cancelled)
                throw new ArgumentException("Only a terminal state can be reconciled.", "terminal");
            lock (_gate)
            {
                // status.json is written by the Worker before job-state.json and SQLite. If the
                // Host exits between those writes, normal transition rules are intentionally too
                // strict (for example queued -> completed or cancelled -> failed). Once there is
                // no live Worker, the terminal disk status is the authoritative recovery record.
                _value["state"] = terminal; _value["message"] = message ?? ""; _value["updatedAt"] = ProviderStore.NowIso();
                if (details != null) _value["details"] = details; else _value.Remove("details");
                Save();
            }
        }

        private void TransitionLocked(string next, string message, JObject details)
        {
            var current = (string)_value["state"] ?? JobStates.Queued;
            if (!JobStates.CanTransition(current, next)) throw new InvalidOperationException("非法 Job 状态迁移：" + current + " -> " + next);
            _value["state"] = next; _value["message"] = message ?? ""; _value["updatedAt"] = ProviderStore.NowIso();
            if (details != null) _value["details"] = details; else _value.Remove("details");
            Save();
        }

        private void Save() { JsonUtil.WriteAtomic(_path, _value); }
    }

    internal sealed class JobEventStore
    {
        private readonly object _gate = new object();
        private readonly string _jobId;
        private readonly string _path;
        private readonly List<long> _lineStarts = new List<long>();
        private long _scanPosition;
        private long _pendingLineStart;
        public JobEventStore(string jobId, string path) { _jobId = jobId; _path = path; }

        public long Count()
        {
            lock (_gate) { RefreshIndex(); return _lineStarts.Count; }
        }

        public JArray ReadAfter(long after, int max, out long next)
        {
            lock (_gate)
            {
                RefreshIndex();
                after = Math.Max(0L, Math.Min(after, _lineStarts.Count)); max = Math.Max(1, Math.Min(max, 1000)); next = after;
                var result = new JArray();
                if (after >= _lineStarts.Count || !File.Exists(_path)) return result;
                var available = Math.Min(max, _lineStarts.Count - (int)after);
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Position = _lineStarts[(int)after];
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
                    {
                        for (var index = 0; index < available; index++)
                        {
                            var line = reader.ReadLine();
                            if (line == null) break;
                            var seq = after + index + 1;
                            result.Add(new JObject { ["id"] = _jobId + ":" + seq, ["seq"] = seq, ["kind"] = "claude-stream", ["payload"] = line });
                            next = seq;
                        }
                    }
                }
                return result;
            }
        }

        private void RefreshIndex()
        {
            if (!File.Exists(_path)) { _lineStarts.Clear(); _scanPosition = 0; _pendingLineStart = 0; return; }
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < _scanPosition)
                {
                    _lineStarts.Clear(); _scanPosition = 0; _pendingLineStart = 0;
                }
                stream.Position = _scanPosition;
                var buffer = new byte[16384];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var index = 0; index < count; index++)
                    {
                        if (buffer[index] != (byte)'\n') continue;
                        _lineStarts.Add(_pendingLineStart);
                        _pendingLineStart = _scanPosition + index + 1;
                    }
                    _scanPosition += count;
                }
            }
        }
    }
}

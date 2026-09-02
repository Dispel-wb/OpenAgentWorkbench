using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    // Artifact persistence is kept in the AgentEventStore transaction boundary,
    // but isolated here so Task/Provider/Terminal changes do not touch its code.
    internal sealed partial class AgentEventStore
    {
        public JObject RecordArtifact(string taskId, string runId, string kind, string path, string mimeType, JObject metadata)
        {
            var full = string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFullPath(path);
            var size = File.Exists(full) ? new FileInfo(full).Length : 0L;
            var sha = File.Exists(full) ? FileSha256(full) : "";
            var id = "artifact:" + runId + ":" + Guid.NewGuid().ToString("N");
            lock (_gate)
            using (var db = SqliteDb.Open(_path))
                db.Execute("INSERT INTO artifacts(id,task_id,run_id,kind,path,mime_type,size_bytes,sha256,metadata_json,created_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)",
                    id, taskId, runId, kind, full, mimeType ?? "application/octet-stream", size, sha,
                    (metadata ?? new JObject()).ToString(Formatting.None), ProviderStore.NowIso());
            return new JObject { ["id"] = id, ["kind"] = kind, ["path"] = full, ["mimeType"] = mimeType, ["size"] = size, ["sha256"] = sha };
        }

        public JArray ListArtifacts(string runId)
        {
            lock (_gate)
            using (var db = SqliteDb.Open(_path))
                return new JArray(db.Query("SELECT id,kind,path,mime_type,size_bytes,sha256,metadata_json,created_at FROM artifacts WHERE run_id=?1 ORDER BY created_at", runId).Select(row => new JObject
                {
                    ["id"] = Convert.ToString(row["id"]), ["kind"] = Convert.ToString(row["kind"]),
                    ["path"] = Convert.ToString(row["path"]), ["mimeType"] = Convert.ToString(row["mime_type"]),
                    ["size"] = Convert.ToInt64(row["size_bytes"]), ["sha256"] = Convert.ToString(row["sha256"]),
                    ["metadata"] = ParseJson(Convert.ToString(row["metadata_json"])), ["createdAt"] = Convert.ToString(row["created_at"])
                }));
        }
    }
}

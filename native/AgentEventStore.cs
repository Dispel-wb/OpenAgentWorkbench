using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class AgentEventStore
    {
        private readonly object _gate = new object();
        private readonly string _path;
        public string Path { get { return _path; } }

        public AgentEventStore() { _path = System.IO.Path.Combine(AppPaths.Data, "agent-store.db"); }

        public void InitializeAndMigrate()
        {
            lock (_gate)
            using (var db = SqliteDb.Open(_path))
            {
                db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
                db.Execute(@"
CREATE TABLE IF NOT EXISTS schema_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS workspaces(id TEXT PRIMARY KEY,path TEXT NOT NULL UNIQUE,name TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY,workspace_path TEXT NOT NULL,title TEXT NOT NULL,state TEXT NOT NULL,archived INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,source_json TEXT);
CREATE INDEX IF NOT EXISTS ix_tasks_workspace_updated ON tasks(workspace_path,updated_at DESC);
CREATE TABLE IF NOT EXISTS turns(id TEXT PRIMARY KEY,task_id TEXT NOT NULL,ordinal INTEGER NOT NULL,state TEXT NOT NULL,prompt TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,FOREIGN KEY(task_id) REFERENCES tasks(id) ON DELETE CASCADE,UNIQUE(task_id,ordinal));
CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY,task_id TEXT,turn_id TEXT,request_id TEXT UNIQUE,model TEXT,workspace_path TEXT,state TEXT NOT NULL,worker_kind TEXT,worker_pid INTEGER,lease_until TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,source_json TEXT);
CREATE INDEX IF NOT EXISTS ix_runs_task_updated ON runs(task_id,updated_at DESC);
CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT,event_id TEXT NOT NULL UNIQUE,run_id TEXT NOT NULL,run_seq INTEGER NOT NULL,task_id TEXT,turn_id TEXT,type TEXT NOT NULL,payload_json TEXT NOT NULL,created_at TEXT NOT NULL,FOREIGN KEY(run_id) REFERENCES runs(id) ON DELETE CASCADE,UNIQUE(run_id,run_seq));
CREATE INDEX IF NOT EXISTS ix_events_run_seq ON events(run_id,run_seq);
CREATE TABLE IF NOT EXISTS approvals(id TEXT PRIMARY KEY,run_id TEXT,tool_name TEXT,state TEXT NOT NULL,input_json TEXT,decision_json TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS tool_calls(id TEXT PRIMARY KEY,run_id TEXT NOT NULL,task_id TEXT,tool_name TEXT NOT NULL,state TEXT NOT NULL,input_json TEXT NOT NULL,output_json TEXT,error_text TEXT,started_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_tool_calls_run_updated ON tool_calls(run_id,updated_at);
CREATE TABLE IF NOT EXISTS schedules(id TEXT PRIMARY KEY,task_id TEXT,state TEXT NOT NULL,due_at TEXT,repeat_minutes INTEGER NOT NULL DEFAULT 0,conflict_policy TEXT,request_id TEXT UNIQUE,payload_json TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,retry_count INTEGER NOT NULL DEFAULT 0,max_retries INTEGER NOT NULL DEFAULT 3,lease_until TEXT,claimed_by TEXT,last_error TEXT,dead_lettered_at TEXT,last_run_at TEXT);
CREATE TABLE IF NOT EXISTS task_queue(id TEXT PRIMARY KEY,task_id TEXT NOT NULL,ordinal INTEGER NOT NULL,kind TEXT NOT NULL,state TEXT NOT NULL,prompt TEXT NOT NULL,request_id TEXT NOT NULL UNIQUE,run_id TEXT,input_offset INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_task_queue_task_state_order ON task_queue(task_id,state,ordinal,created_at);
CREATE TABLE IF NOT EXISTS artifacts(id TEXT PRIMARY KEY,task_id TEXT,run_id TEXT,kind TEXT NOT NULL,path TEXT,mime_type TEXT,size_bytes INTEGER,sha256 TEXT,metadata_json TEXT,created_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_artifacts_run ON artifacts(run_id,created_at);
CREATE TABLE IF NOT EXISTS context_entries(id TEXT PRIMARY KEY,run_id TEXT NOT NULL,task_id TEXT,source_type TEXT NOT NULL,label TEXT,estimated_tokens INTEGER NOT NULL,content_sha256 TEXT,metadata_json TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_context_entries_run ON context_entries(run_id,created_at);
CREATE TABLE IF NOT EXISTS task_forks(id TEXT PRIMARY KEY,parent_task_id TEXT NOT NULL,child_task_id TEXT NOT NULL,checkpoint_id TEXT,created_at TEXT NOT NULL,metadata_json TEXT NOT NULL,UNIQUE(child_task_id));
CREATE INDEX IF NOT EXISTS ix_task_forks_parent ON task_forks(parent_task_id,created_at);
CREATE TABLE IF NOT EXISTS snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,aggregate_type TEXT NOT NULL,aggregate_id TEXT NOT NULL,event_seq INTEGER NOT NULL,state_json TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_snapshots_aggregate ON snapshots(aggregate_type,aggregate_id,event_seq DESC);
CREATE TABLE IF NOT EXISTS migration_sources(path TEXT PRIMARY KEY,size_bytes INTEGER NOT NULL,modified_utc TEXT NOT NULL,sha256 TEXT NOT NULL,imported_at TEXT NOT NULL);
INSERT OR REPLACE INTO schema_meta(key,value) VALUES('schema_version','5');
");
                if (!db.Query("PRAGMA table_info(task_queue)").Any(row => string.Equals(Convert.ToString(row["name"]), "input_offset", StringComparison.OrdinalIgnoreCase)))
                    db.Execute("ALTER TABLE task_queue ADD COLUMN input_offset INTEGER NOT NULL DEFAULT 0");
                EnsureScheduleColumns(db);
                if (db.ScalarString("SELECT value FROM schema_meta WHERE key='legacy_migration_complete'") != "1") MigrateLegacy(db);
            }
        }

        private static void EnsureScheduleColumns(SqliteDb db)
        {
            var columns = new HashSet<string>(db.Query("PRAGMA table_info(schedules)").Select(row => Convert.ToString(row["name"])), StringComparer.OrdinalIgnoreCase);
            var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "retry_count", "INTEGER NOT NULL DEFAULT 0" }, { "max_retries", "INTEGER NOT NULL DEFAULT 3" },
                { "lease_until", "TEXT" }, { "claimed_by", "TEXT" }, { "last_error", "TEXT" },
                { "dead_lettered_at", "TEXT" }, { "last_run_at", "TEXT" }
            };
            foreach (var pair in additions) if (!columns.Contains(pair.Key)) db.Execute("ALTER TABLE schedules ADD COLUMN " + pair.Key + " " + pair.Value);
        }

        private void MigrateLegacy(SqliteDb db)
        {
            var backup = System.IO.Path.Combine(AppPaths.Data, "migration-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);
            foreach (var file in LegacyFiles())
            {
                try
                {
                    var relative = Relative(AppPaths.Data, file);
                    var target = System.IO.Path.Combine(backup, relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    File.Copy(file, target, true);
                }
                catch (Exception error) { CrashLog.Write("EventStoreBackup", error); }
            }
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                ImportTasks(db);
                ImportRuns(db);
                ImportSchedules(db);
                db.Execute("INSERT OR REPLACE INTO schema_meta(key,value) VALUES('legacy_migration_complete','1')");
                db.Execute("INSERT OR REPLACE INTO schema_meta(key,value) VALUES('legacy_backup_path',?1)", backup);
                db.Execute("COMMIT");
            }
            catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
        }

        private IEnumerable<string> LegacyFiles()
        {
            var roots = new[] { AppPaths.SessionsFile, AppPaths.SettingsFile, System.IO.Path.Combine(AppPaths.Data, "schedules.json") };
            foreach (var file in roots.Where(File.Exists)) yield return file;
            foreach (var directory in new[] { AppPaths.Messages, AppPaths.Runs })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).Concat(Directory.GetFiles(directory, "*.jsonl", SearchOption.AllDirectories))) yield return file;
            }
        }

        private void ImportTasks(SqliteDb db)
        {
            var sessions = JsonUtil.Read(AppPaths.SessionsFile, new JArray()) as JArray ?? new JArray();
            foreach (var session in sessions.OfType<JObject>())
            {
                var id = (string)session["id"] ?? Guid.NewGuid().ToString();
                var workspace = (string)session["workspace"] ?? AppPaths.Workspace;
                var now = ProviderStore.NowIso();
                db.Execute(@"INSERT OR IGNORE INTO tasks(id,workspace_path,title,state,archived,created_at,updated_at,source_json)
VALUES(?1,?2,?3,?4,?5,?6,?7,?8)", id, workspace, (string)session["title"] ?? "历史任务", (bool?)session["archived"] == true ? "archived" : "draft",
                    (bool?)session["archived"] == true ? 1 : 0, (string)session["createdAt"] ?? now, (string)session["updatedAt"] ?? now, session.ToString(Formatting.None));
                var messagePath = System.IO.Path.Combine(AppPaths.Messages, id + ".json");
                if (File.Exists(messagePath))
                {
                    EnsureRun(db, "legacy-task-" + id, id, "", workspace, "completed", "legacy-json", now, "");
                    AppendEvent(db, "legacy-task-" + id, id, null, "legacy.messages", File.ReadAllText(messagePath, Encoding.UTF8), "legacy:messages:" + id, now);
                    TrackSource(db, messagePath);
                }
            }
            if (File.Exists(AppPaths.SessionsFile)) TrackSource(db, AppPaths.SessionsFile);
        }

        private void ImportRuns(SqliteDb db)
        {
            if (!Directory.Exists(AppPaths.Runs)) return;
            foreach (var root in Directory.GetDirectories(AppPaths.Runs))
            {
                var id = System.IO.Path.GetFileName(root);
                var requestPath = System.IO.Path.Combine(root, "request.json");
                var statusPath = System.IO.Path.Combine(root, "status.json");
                var streamPath = System.IO.Path.Combine(root, "stream.jsonl");
                var request = JsonUtil.Read(requestPath, new JObject()) as JObject ?? new JObject();
                var status = JsonUtil.Read(statusPath, new JObject()) as JObject ?? new JObject();
                var taskId = (string)request["guiSessionId"] ?? (string)request["sessionId"];
                var workspace = (string)request["workspace"] ?? AppPaths.Workspace;
                var now = ProviderStore.NowIso();
                if (!string.IsNullOrWhiteSpace(taskId))
                    db.Execute("INSERT OR IGNORE INTO tasks(id,workspace_path,title,state,archived,created_at,updated_at,source_json) VALUES(?1,?2,?3,?4,0,?5,?5,?6)", taskId, workspace, "恢复的 Agent 任务", (string)status["state"] ?? "running", now, "{}");
                EnsureRun(db, id, taskId, (string)request["model"] ?? "", workspace, (string)status["state"] ?? "running", "legacy-jsonl", now, (string)request["requestId"] ?? "");
                if (File.Exists(streamPath))
                {
                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(streamPath, Encoding.UTF8))
                    {
                        lineNumber++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        AppendEvent(db, id, taskId, null, "claude-stream", line, "legacy:" + id + ":" + lineNumber, now);
                    }
                    TrackSource(db, streamPath);
                }
                if (File.Exists(requestPath)) TrackSource(db, requestPath);
                if (File.Exists(statusPath)) TrackSource(db, statusPath);
            }
        }

        private void ImportSchedules(SqliteDb db)
        {
            var path = System.IO.Path.Combine(AppPaths.Data, "schedules.json");
            var values = JsonUtil.Read(path, new JArray()) as JArray ?? new JArray();
            foreach (var value in values.OfType<JObject>())
            {
                var id = (string)value["id"] ?? Guid.NewGuid().ToString(); var now = ProviderStore.NowIso();
                db.Execute("INSERT OR IGNORE INTO schedules(id,task_id,state,due_at,repeat_minutes,conflict_policy,request_id,payload_json,created_at,updated_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?9)",
                    id, (string)value["sessionId"], (bool?)value["enabled"] == false ? "disabled" : "scheduled", (string)value["at"], (long?)value["repeatMinutes"] ?? 0L,
                    (string)value["conflictPolicy"] ?? "queue", "legacy-schedule:" + id, value.ToString(Formatting.None), now);
            }
            if (File.Exists(path)) TrackSource(db, path);
        }

        private static void EnsureRun(SqliteDb db, string id, string taskId, string model, string workspace, string state, string worker, string now, string requestId)
        {
            db.Execute(@"INSERT OR IGNORE INTO runs(id,task_id,request_id,model,workspace_path,state,worker_kind,created_at,updated_at,source_json)
VALUES(?1,?2,NULLIF(?3,''),?4,?5,?6,?7,?8,?8,'{}')", id, taskId, requestId, model, workspace, state, worker, now);
        }

        private static long AppendEvent(SqliteDb db, string runId, string taskId, string turnId, string type, string payload, string eventId, string now)
        {
            var existing = db.ScalarInt64("SELECT COALESCE((SELECT run_seq FROM events WHERE event_id=?1),0)", eventId);
            if (existing > 0) return existing;
            var next = db.ScalarInt64("SELECT COALESCE(MAX(run_seq),0)+1 FROM events WHERE run_id=?1", runId);
            db.Execute("INSERT OR IGNORE INTO events(event_id,run_id,run_seq,task_id,turn_id,type,payload_json,created_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8)", eventId, runId, next, taskId, turnId, type, payload ?? "", now);
            return db.ScalarInt64("SELECT run_seq FROM events WHERE event_id=?1", eventId);
        }

        private static void TrackSource(SqliteDb db, string path)
        {
            var info = new FileInfo(path);
            db.Execute("INSERT OR REPLACE INTO migration_sources(path,size_bytes,modified_utc,sha256,imported_at) VALUES(?1,?2,?3,?4,?5)",
                path, info.Length, info.LastWriteTimeUtc.ToString("o"), FileSha256(path), ProviderStore.NowIso());
        }

        public void UpsertTask(string id, string workspace, string title, string state, bool archived, string sourceJson)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var now = ProviderStore.NowIso();
                db.Execute(@"INSERT INTO tasks(id,workspace_path,title,state,archived,created_at,updated_at,source_json) VALUES(?1,?2,?3,?4,?5,?6,?6,?7)
ON CONFLICT(id) DO UPDATE SET workspace_path=excluded.workspace_path,title=excluded.title,state=excluded.state,archived=excluded.archived,updated_at=excluded.updated_at,source_json=excluded.source_json",
                    id, workspace, title, state, archived ? 1 : 0, now, sourceJson ?? "{}");
            }
        }

        public void UpsertRun(string id, string taskId, string requestId, string model, string workspace, string state, int workerPid)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var now = ProviderStore.NowIso();
                db.Execute(@"INSERT INTO runs(id,task_id,request_id,model,workspace_path,state,worker_kind,worker_pid,lease_until,created_at,updated_at,source_json)
VALUES(?1,?2,NULLIF(?3,''),?4,?5,?6,'native',?7,?8,?9,?9,'{}')
ON CONFLICT(id) DO UPDATE SET state=excluded.state,worker_kind='native',worker_pid=excluded.worker_pid,lease_until=excluded.lease_until,updated_at=excluded.updated_at",
                    id, taskId, requestId, model, workspace, state, workerPid, DateTimeOffset.Now.AddSeconds(8).ToString("o"), now);
            }
        }

        public void UpdateRunState(string id, string state, int workerPid, string details)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
                db.Execute("UPDATE runs SET state=?2,worker_pid=?3,lease_until=?4,updated_at=?5,source_json=?6 WHERE id=?1", id, state, workerPid,
                    DateTimeOffset.Now.AddSeconds(8).ToString("o"), ProviderStore.NowIso(), details ?? "{}");
        }

        public void RecordApproval(string id, string runId, string toolName, string state, JToken input, JToken decision)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var now = ProviderStore.NowIso();
                db.Execute(@"INSERT INTO approvals(id,run_id,tool_name,state,input_json,decision_json,created_at,updated_at)
VALUES(?1,?2,?3,?4,?5,?6,?7,?7)
ON CONFLICT(id) DO UPDATE SET state=excluded.state,decision_json=excluded.decision_json,updated_at=excluded.updated_at",
                    id, runId, toolName, state, input == null ? "{}" : input.ToString(Formatting.None), decision == null ? "{}" : decision.ToString(Formatting.None), now);
            }
        }

        public JObject RecordFork(string parentTaskId, string childTaskId, string checkpointId, JObject metadata)
        {
            var id = "fork:" + childTaskId; var now = ProviderStore.NowIso();
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("INSERT OR REPLACE INTO task_forks(id,parent_task_id,child_task_id,checkpoint_id,created_at,metadata_json) VALUES(?1,?2,?3,?4,?5,?6)",
                id, parentTaskId ?? "", childTaskId ?? "", checkpointId ?? "", now, (metadata ?? new JObject()).ToString(Formatting.None));
            return new JObject { ["id"] = id, ["parentTaskId"] = parentTaskId, ["childTaskId"] = childTaskId, ["checkpointId"] = checkpointId, ["createdAt"] = now };
        }

        public JArray ListForks(string taskId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT id,parent_task_id,child_task_id,checkpoint_id,created_at,metadata_json FROM task_forks WHERE parent_task_id=?1 OR child_task_id=?1 ORDER BY created_at", taskId ?? "").Select(row => new JObject
            {
                ["id"] = Convert.ToString(row["id"]), ["parentTaskId"] = Convert.ToString(row["parent_task_id"]), ["childTaskId"] = Convert.ToString(row["child_task_id"]),
                ["checkpointId"] = Convert.ToString(row["checkpoint_id"]), ["createdAt"] = Convert.ToString(row["created_at"]), ["metadata"] = ParseJson(Convert.ToString(row["metadata_json"]))
            }));
        }

        public JArray ListSchedules(bool includeDeadLetters = true)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var sql = includeDeadLetters ? "SELECT * FROM schedules ORDER BY due_at,created_at" : "SELECT * FROM schedules WHERE state<>'dead_letter' ORDER BY due_at,created_at";
                return new JArray(db.Query(sql).Select(ScheduleItem));
            }
        }

        public int ReplaceSchedules(JArray values)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var value in (values ?? new JArray()).OfType<JObject>())
                    {
                        var id = SafeIdentifier((string)value["id"]); if (id.Length == 0) id = Guid.NewGuid().ToString();
                        keep.Add(id); var now = ProviderStore.NowIso(); DateTimeOffset due;
                        if (!DateTimeOffset.TryParse((string)value["at"], out due)) due = DateTimeOffset.Now.AddMinutes(5);
                        var enabled = (bool?)value["enabled"] ?? true; var state = enabled ? "scheduled" : "disabled";
                        var retry = Math.Max(0, Math.Min(20, (int?)value["maxRetries"] ?? 3));
                        db.Execute(@"INSERT INTO schedules(id,task_id,state,due_at,repeat_minutes,conflict_policy,request_id,payload_json,created_at,updated_at,retry_count,max_retries)
VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?9,0,?10)
ON CONFLICT(id) DO UPDATE SET task_id=excluded.task_id,state=CASE WHEN schedules.state IN ('running','dead_letter') THEN schedules.state ELSE excluded.state END,due_at=CASE WHEN schedules.state='dead_letter' THEN schedules.due_at ELSE excluded.due_at END,repeat_minutes=excluded.repeat_minutes,conflict_policy=excluded.conflict_policy,payload_json=excluded.payload_json,max_retries=excluded.max_retries,updated_at=excluded.updated_at",
                            id, (string)value["sessionId"] ?? "", state, due.ToUniversalTime().ToString("o"), Math.Max(0, (int?)value["repeatMinutes"] ?? 0),
                            (string)value["conflictPolicy"] ?? "queue", "schedule:" + id, value.ToString(Formatting.None), now, retry);
                    }
                    foreach (var row in db.Query("SELECT id FROM schedules WHERE state<>'running'"))
                    {
                        var id = Convert.ToString(row["id"]); if (!keep.Contains(id)) db.Execute("DELETE FROM schedules WHERE id=?1", id);
                    }
                    db.Execute("COMMIT"); return keep.Count;
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
            }
        }

        public JArray ClaimDueSchedules(string owner, DateTimeOffset now, int limit)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    var stamp = now.ToUniversalTime().ToString("o");
                    db.Execute("UPDATE schedules SET state='retry',claimed_by=NULL,lease_until=NULL,updated_at=?1,last_error=CASE WHEN COALESCE(last_error,'')='' THEN 'Host lease expired; execution recovered' ELSE last_error END WHERE state='running' AND lease_until IS NOT NULL AND lease_until<?1", stamp);
                    var rows = db.Query("SELECT * FROM schedules WHERE state IN ('scheduled','retry') AND due_at<=?1 ORDER BY due_at,created_at LIMIT ?2", stamp, Math.Max(1, Math.Min(limit, 50)));
                    var claimed = new JArray(); var lease = now.AddMinutes(2).ToUniversalTime().ToString("o");
                    foreach (var row in rows)
                    {
                        var id = Convert.ToString(row["id"]);
                        db.Execute("UPDATE schedules SET state='running',claimed_by=?2,lease_until=?3,updated_at=?4 WHERE id=?1 AND state IN ('scheduled','retry')", id, owner, lease, stamp);
                        if (db.ScalarInt64("SELECT changes()") == 1) claimed.Add(ScheduleItem(db.Query("SELECT * FROM schedules WHERE id=?1", id).First()));
                    }
                    db.Execute("COMMIT"); return claimed;
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
            }
        }

        public void CompleteSchedule(string id, bool success, string error, DateTimeOffset now)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM schedules WHERE id=?1", id); if (rows.Count == 0) return;
                var row = rows[0]; var repeat = Convert.ToInt32(row["repeat_minutes"]); var retries = Convert.ToInt32(row["retry_count"]); var max = Convert.ToInt32(row["max_retries"]);
                var stamp = now.ToUniversalTime().ToString("o");
                if (success)
                {
                    var state = repeat > 0 ? "scheduled" : "completed"; var due = repeat > 0 ? now.AddMinutes(repeat).ToUniversalTime().ToString("o") : Convert.ToString(row["due_at"]);
                    db.Execute("UPDATE schedules SET state=?2,due_at=?3,retry_count=0,lease_until=NULL,claimed_by=NULL,last_error=NULL,last_run_at=?4,updated_at=?4 WHERE id=?1", id, state, due, stamp);
                }
                else if (retries < max)
                {
                    var nextRetry = retries + 1; var delaySeconds = Math.Min(1800, 15 * (1 << Math.Min(nextRetry - 1, 7)));
                    db.Execute("UPDATE schedules SET state='retry',due_at=?2,retry_count=?3,lease_until=NULL,claimed_by=NULL,last_error=?4,last_run_at=?5,updated_at=?5 WHERE id=?1", id, now.AddSeconds(delaySeconds).ToUniversalTime().ToString("o"), nextRetry, ScheduleLimit(error, 2000), stamp);
                }
                else db.Execute("UPDATE schedules SET state='dead_letter',lease_until=NULL,claimed_by=NULL,last_error=?2,dead_lettered_at=?3,last_run_at=?3,updated_at=?3 WHERE id=?1", id, ScheduleLimit(error, 2000), stamp);
            }
        }

        public void DeferSchedule(string id, DateTimeOffset due, string reason)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("UPDATE schedules SET state='scheduled',due_at=?2,lease_until=NULL,claimed_by=NULL,last_error=?3,updated_at=?4 WHERE id=?1", id, due.ToUniversalTime().ToString("o"), ScheduleLimit(reason, 500), ProviderStore.NowIso());
        }

        public bool ReplayDeadLetter(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("UPDATE schedules SET state='scheduled',due_at=?2,retry_count=0,lease_until=NULL,claimed_by=NULL,last_error=NULL,dead_lettered_at=NULL,updated_at=?2 WHERE id=?1 AND state='dead_letter'", id, ProviderStore.NowIso());
                return db.ScalarInt64("SELECT changes()") == 1;
            }
        }

        private static JObject ScheduleItem(Dictionary<string, object> row)
        {
            JObject payload; try { payload = JObject.Parse(Convert.ToString(row["payload_json"])); } catch { payload = new JObject(); }
            var state = Convert.ToString(row["state"]); payload["id"] = Convert.ToString(row["id"]); payload["sessionId"] = Convert.ToString(row["task_id"]);
            payload["at"] = Convert.ToString(row["due_at"]); payload["repeatMinutes"] = Convert.ToInt32(row["repeat_minutes"]); payload["conflictPolicy"] = Convert.ToString(row["conflict_policy"]);
            payload["enabled"] = state == "scheduled" || state == "retry" || state == "running"; payload["state"] = state;
            payload["retryCount"] = Convert.ToInt32(row["retry_count"]); payload["maxRetries"] = Convert.ToInt32(row["max_retries"]);
            payload["lastError"] = Convert.ToString(row["last_error"]); payload["lastRunAt"] = Convert.ToString(row["last_run_at"]); payload["leaseUntil"] = Convert.ToString(row["lease_until"]);
            return payload;
        }

        private static string SafeIdentifier(string value) { return new string((value ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').Take(120).ToArray()); }
        private static string ScheduleLimit(string value, int max) { value = value ?? ""; return value.Length <= max ? value : value.Substring(0, max); }

        public JObject Enqueue(string taskId, string prompt, string kind, JObject payload)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var id = Guid.NewGuid().ToString(); var now = ProviderStore.NowIso();
                var ordinal = db.ScalarInt64("SELECT COALESCE(MAX(ordinal),0)+1 FROM task_queue WHERE task_id=?1 AND state='queued'", taskId);
                var requestId = "queue:" + id;
                db.Execute("INSERT INTO task_queue(id,task_id,ordinal,kind,state,prompt,request_id,payload_json,created_at,updated_at) VALUES(?1,?2,?3,?4,'queued',?5,?6,?7,?8,?8)",
                    id, taskId, ordinal, string.IsNullOrWhiteSpace(kind) ? "queued" : kind, prompt ?? "", requestId, (payload ?? new JObject()).ToString(Formatting.None), now);
                return QueueItem(db.Query("SELECT * FROM task_queue WHERE id=?1", id).First());
            }
        }

        public JArray ListQueue(string taskId, bool includeTerminal = false)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var sql = includeTerminal ? "SELECT * FROM task_queue WHERE task_id=?1 ORDER BY ordinal,created_at" : "SELECT * FROM task_queue WHERE task_id=?1 AND state IN ('queued','starting','running','paused') ORDER BY ordinal,created_at";
                return new JArray(db.Query(sql, taskId).Select(QueueItem));
            }
        }

        public JArray QueuedItems()
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT * FROM task_queue WHERE state='queued' ORDER BY ordinal,created_at LIMIT 200").Select(QueueItem));
        }

        public JArray QueueInProgress()
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT * FROM task_queue WHERE state IN ('starting','running') ORDER BY updated_at").Select(QueueItem));
        }

        public JObject QueueById(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) { var rows = db.Query("SELECT * FROM task_queue WHERE id=?1", id); return rows.Count == 0 ? null : QueueItem(rows[0]); }
        }

        public bool TransitionQueue(string id, string expected, string state, string runId = null, long inputOffset = 0)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("UPDATE task_queue SET state=?3,run_id=COALESCE(NULLIF(?4,''),run_id),input_offset=CASE WHEN ?5>0 THEN ?5 ELSE input_offset END,updated_at=?6 WHERE id=?1 AND state=?2", id, expected, state, runId ?? "", inputOffset, ProviderStore.NowIso());
                return db.ScalarInt64("SELECT changes()") == 1;
            }
        }

        public bool TransitionQueueAny(string id, string state, string runId = null, long inputOffset = 0)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("UPDATE task_queue SET state=?2,run_id=COALESCE(NULLIF(?3,''),run_id),input_offset=CASE WHEN ?4>0 THEN ?4 ELSE input_offset END,updated_at=?5 WHERE id=?1", id, state, runId ?? "", inputOffset, ProviderStore.NowIso());
                return db.ScalarInt64("SELECT changes()") == 1;
            }
        }

        public bool CancelQueue(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("UPDATE task_queue SET state='cancelled',updated_at=?2 WHERE id=?1 AND state IN ('queued','paused')", id, ProviderStore.NowIso());
                return db.ScalarInt64("SELECT changes()") == 1;
            }
        }

        public bool PrioritizeQueue(string id, bool steer)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var taskId = db.ScalarString("SELECT task_id FROM task_queue WHERE id=?1 AND state='queued'", id); if (taskId == null) return false;
                var ordinal = db.ScalarInt64("SELECT COALESCE(MIN(ordinal),1)-1 FROM task_queue WHERE task_id=?1 AND state='queued'", taskId);
                db.Execute("UPDATE task_queue SET ordinal=?2,kind=?3,updated_at=?4 WHERE id=?1 AND state='queued'", id, ordinal, steer ? "steer" : "queued", ProviderStore.NowIso());
                return true;
            }
        }

        private static JObject QueueItem(Dictionary<string, object> row)
        {
            JObject payload; try { payload = JObject.Parse(Convert.ToString(row["payload_json"])); } catch { payload = new JObject(); }
            return new JObject
            {
                ["id"] = Convert.ToString(row["id"]), ["taskId"] = Convert.ToString(row["task_id"]), ["ordinal"] = Convert.ToInt64(row["ordinal"]),
                ["kind"] = Convert.ToString(row["kind"]), ["state"] = Convert.ToString(row["state"]), ["text"] = Convert.ToString(row["prompt"]),
                ["requestId"] = Convert.ToString(row["request_id"]), ["runId"] = Convert.ToString(row["run_id"]), ["payload"] = payload,
                ["inputOffset"] = row.ContainsKey("input_offset") && row["input_offset"] != null ? Convert.ToInt64(row["input_offset"]) : 0L,
                ["createdAt"] = Convert.ToString(row["created_at"]), ["updatedAt"] = Convert.ToString(row["updated_at"])
            };
        }

        public long AppendWorkerEvent(string runId, string taskId, string payload, string eventId = null)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    var stableId = string.IsNullOrWhiteSpace(eventId) ? "native:" + runId + ":" + Guid.NewGuid().ToString("N") : eventId;
                    var now = ProviderStore.NowIso();
                    var next = AppendEvent(db, runId, taskId, null, "claude-stream", payload, stableId, now);
                    NormalizeToolCalls(db, runId, taskId, payload, now);
                    db.Execute("COMMIT"); return next;
                }
                catch { try { db.Execute("ROLLBACK"); } catch { } throw; }
            }
        }

        public long EventCount(string runId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return db.ScalarInt64("SELECT COALESCE(MAX(run_seq),0) FROM events WHERE run_id=?1", runId);
        }

        public JArray ReadEvents(string runId, long after, int max, out long next)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT run_seq,event_id,type,payload_json FROM events WHERE run_id=?1 AND run_seq>?2 ORDER BY run_seq LIMIT ?3", runId, after, Math.Max(1, Math.Min(max, 1000)));
                var result = new JArray(); next = after;
                foreach (var row in rows)
                {
                    var seq = Convert.ToInt64(row["run_seq"]); next = seq;
                    result.Add(new JObject { ["id"] = Convert.ToString(row["event_id"]), ["seq"] = seq, ["kind"] = Convert.ToString(row["type"]), ["payload"] = Convert.ToString(row["payload_json"]) });
                }
                return result;
            }
        }

        public JArray ReadToolCalls(string runId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT id,tool_name,state,input_json,output_json,error_text,started_at,updated_at FROM tool_calls WHERE run_id=?1 ORDER BY started_at", runId).Select(row => new JObject
            {
                ["id"] = Convert.ToString(row["id"]), ["toolName"] = Convert.ToString(row["tool_name"]), ["state"] = Convert.ToString(row["state"]),
                ["input"] = ParseJson(Convert.ToString(row["input_json"])), ["output"] = ParseJson(Convert.ToString(row["output_json"])),
                ["error"] = Convert.ToString(row["error_text"]), ["startedAt"] = Convert.ToString(row["started_at"]), ["updatedAt"] = Convert.ToString(row["updated_at"])
            }));
        }

        public void RecordContext(string runId, string taskId, string sourceType, string label, string content, JObject metadata)
        {
            content = content ?? ""; var hash = Sha256Text(content); var id = "context:" + runId + ":" + sourceType + ":" + hash.Substring(0, 16);
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("INSERT OR REPLACE INTO context_entries(id,run_id,task_id,source_type,label,estimated_tokens,content_sha256,metadata_json,created_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)",
                id, runId, taskId, sourceType, label ?? "", EstimateTokens(content), hash, (metadata ?? new JObject()).ToString(Formatting.None), ProviderStore.NowIso());
        }

        public JObject ContextReport(string runId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT source_type,label,estimated_tokens,content_sha256,metadata_json,created_at FROM context_entries WHERE run_id=?1 ORDER BY created_at", runId);
                return new JObject
                {
                    ["runId"] = runId, ["estimatedTokens"] = rows.Sum(row => Convert.ToInt64(row["estimated_tokens"])),
                    ["sources"] = new JArray(rows.Select(row => new JObject { ["type"] = Convert.ToString(row["source_type"]), ["label"] = Convert.ToString(row["label"]), ["estimatedTokens"] = Convert.ToInt64(row["estimated_tokens"]), ["sha256"] = Convert.ToString(row["content_sha256"]), ["metadata"] = ParseJson(Convert.ToString(row["metadata_json"])), ["createdAt"] = Convert.ToString(row["created_at"]) }))
                };
            }
        }

        public JObject RecordArtifact(string taskId, string runId, string kind, string path, string mimeType, JObject metadata)
        {
            var full = string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFullPath(path); var size = File.Exists(full) ? new FileInfo(full).Length : 0L;
            var sha = File.Exists(full) ? FileSha256(full) : ""; var id = "artifact:" + runId + ":" + Guid.NewGuid().ToString("N");
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("INSERT INTO artifacts(id,task_id,run_id,kind,path,mime_type,size_bytes,sha256,metadata_json,created_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10)",
                id, taskId, runId, kind, full, mimeType ?? "application/octet-stream", size, sha, (metadata ?? new JObject()).ToString(Formatting.None), ProviderStore.NowIso());
            return new JObject { ["id"] = id, ["kind"] = kind, ["path"] = full, ["mimeType"] = mimeType, ["size"] = size, ["sha256"] = sha };
        }

        public JArray ListArtifacts(string runId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT id,kind,path,mime_type,size_bytes,sha256,metadata_json,created_at FROM artifacts WHERE run_id=?1 ORDER BY created_at", runId).Select(row => new JObject
            { ["id"] = Convert.ToString(row["id"]), ["kind"] = Convert.ToString(row["kind"]), ["path"] = Convert.ToString(row["path"]), ["mimeType"] = Convert.ToString(row["mime_type"]), ["size"] = Convert.ToInt64(row["size_bytes"]), ["sha256"] = Convert.ToString(row["sha256"]), ["metadata"] = ParseJson(Convert.ToString(row["metadata_json"])), ["createdAt"] = Convert.ToString(row["created_at"]) }));
        }

        public string FindRunByRequestId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return "";
            lock (_gate) using (var db = SqliteDb.Open(_path)) return db.ScalarString("SELECT id FROM runs WHERE request_id=?1", requestId) ?? "";
        }

        public JObject Health()
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JObject
            {
                ["path"] = _path, ["schemaVersion"] = db.ScalarString("SELECT value FROM schema_meta WHERE key='schema_version'") ?? "0",
                ["journalMode"] = db.ScalarString("PRAGMA journal_mode") ?? "", ["tasks"] = db.ScalarInt64("SELECT COUNT(*) FROM tasks"),
                ["runs"] = db.ScalarInt64("SELECT COUNT(*) FROM runs"), ["events"] = db.ScalarInt64("SELECT COUNT(*) FROM events"), ["toolCalls"] = db.ScalarInt64("SELECT COUNT(*) FROM tool_calls"),
                ["artifacts"] = db.ScalarInt64("SELECT COUNT(*) FROM artifacts"), ["contextEntries"] = db.ScalarInt64("SELECT COUNT(*) FROM context_entries")
                , ["integrity"] = db.ScalarString("PRAGMA integrity_check") ?? "unknown"
            };
        }

        private static void NormalizeToolCalls(SqliteDb db, string runId, string taskId, string payload, string now)
        {
            JObject value; try { value = JObject.Parse(payload); } catch { return; }
            var type = (string)value["type"] ?? ""; var blocks = value["message"]?["content"] as JArray ?? new JArray();
            if (type == "assistant")
            {
                foreach (var block in blocks.OfType<JObject>().Where(block => (string)block["type"] == "tool_use"))
                {
                    var id = (string)block["id"] ?? ""; if (id.Length == 0) continue;
                    db.Execute("INSERT OR IGNORE INTO tool_calls(id,run_id,task_id,tool_name,state,input_json,started_at,updated_at) VALUES(?1,?2,?3,?4,'running',?5,?6,?6)",
                        id, runId, taskId, (string)block["name"] ?? "unknown", (block["input"] ?? new JObject()).ToString(Formatting.None), now);
                }
            }
            else if (type == "user")
            {
                foreach (var block in blocks.OfType<JObject>().Where(block => (string)block["type"] == "tool_result"))
                {
                    var id = (string)block["tool_use_id"] ?? ""; if (id.Length == 0) continue;
                    var output = (block["content"] ?? JValue.CreateString("")).ToString(Formatting.None);
                    if (output.Length > 1000000) output = output.Substring(0, 700000) + "\n...tool output capped...\n" + output.Substring(output.Length - 200000);
                    var failed = (bool?)block["is_error"] ?? false;
                    db.Execute("UPDATE tool_calls SET state=?2,output_json=?3,error_text=?4,updated_at=?5 WHERE id=?1", id, failed ? "failed" : "completed", output, failed ? LimitText(output, 12000) : "", now);
                }
            }
        }

        private static JToken ParseJson(string value) { if (string.IsNullOrWhiteSpace(value)) return null; try { return JToken.Parse(value); } catch { return JValue.CreateString(value); } }
        private static string LimitText(string value, int max) { return string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value.Substring(0, max); }
        private static long EstimateTokens(string value) { if (string.IsNullOrEmpty(value)) return 0L; long wide = value.Count(character => character > 255); return Math.Max(1L, (value.Length - wide + 3L) / 4L + wide); }
        private static string Sha256Text(string value) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(item => item.ToString("X2"))); }

        private static string Relative(string root, string path)
        {
            var prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var full = System.IO.Path.GetFullPath(path);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : System.IO.Path.GetFileName(full);
        }
        private static string FileSha256(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("X2"))); }
    }

    internal static class AgentEventStoreSelfTest
    {
        public static int Run()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Messages);
                Directory.CreateDirectory(AppPaths.Runs);
                var taskId = "task-utf8";
                JsonUtil.WriteAtomic(AppPaths.SessionsFile, new JArray(new JObject
                {
                    ["id"] = taskId, ["title"] = "中文迁移任务", ["workspace"] = AppPaths.Workspace,
                    ["createdAt"] = ProviderStore.NowIso(), ["updatedAt"] = ProviderStore.NowIso()
                }));
                JsonUtil.WriteAtomic(System.IO.Path.Combine(AppPaths.Messages, taskId + ".json"), new JArray(new JObject { ["role"] = "user", ["text"] = "迁移中文消息" }));
                var runId = "run-legacy"; var runRoot = System.IO.Path.Combine(AppPaths.Runs, runId); Directory.CreateDirectory(runRoot);
                JsonUtil.WriteAtomic(System.IO.Path.Combine(runRoot, "request.json"), new JObject { ["guiSessionId"] = taskId, ["sessionId"] = taskId, ["workspace"] = AppPaths.Workspace, ["model"] = "offline" });
                JsonUtil.WriteAtomic(System.IO.Path.Combine(runRoot, "status.json"), new JObject { ["state"] = "completed" });
                File.WriteAllLines(System.IO.Path.Combine(runRoot, "stream.jsonl"), new[] { "{\"type\":\"assistant\",\"text\":\"第一条\"}", "{\"type\":\"result\",\"is_error\":false}" }, new UTF8Encoding(false));
                JsonUtil.WriteAtomic(System.IO.Path.Combine(AppPaths.Data, "schedules.json"), new JArray(new JObject { ["id"] = "schedule-one", ["sessionId"] = taskId, ["enabled"] = true, ["at"] = DateTimeOffset.Now.AddHours(1).ToString("o") }));

                var store = new AgentEventStore(); store.InitializeAndMigrate(); var first = store.Health();
                if ((string)first["journalMode"] != "wal" || (string)first["integrity"] != "ok") return 31;
                if ((long)first["tasks"] < 1 || (long)first["runs"] < 2 || (long)first["events"] < 3) return 32;
                var eventCount = (long)first["events"];
                new AgentEventStore().InitializeAndMigrate(); var second = store.Health();
                if ((long)second["events"] != eventCount) return 33;
                store.UpsertTask("task-live", AppPaths.Workspace, "实时任务", JobStates.Running, false, "{}");
                store.UpsertRun("run-live", "task-live", "request-live", "offline", AppPaths.Workspace, JobStates.Running, 123);
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"result\",\"text\":\"SQLite 中文\"}");
                long next; var events = store.ReadEvents("run-live", 0, 10, out next);
                if (next != 1 || events.Count != 1 || !((string)events[0]["payload"]).Contains("SQLite 中文")) return 34;
                if (store.FindRunByRequestId("request-live") != "run-live") return 35;
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-one\",\"name\":\"Read\",\"input\":{\"file_path\":\"demo.txt\"}}]}}", "test:tool:start");
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-one\",\"content\":\"ok\"}]}}", "test:tool:result");
                var tools = store.ReadToolCalls("run-live");
                if (tools.Count != 1 || (string)tools[0]["state"] != "completed" || (string)tools[0]["toolName"] != "Read") return 36;
                store.RecordContext("run-live", "task-live", "user", "测试上下文", "中文上下文", new JObject());
                if ((long)store.ContextReport("run-live")["estimatedTokens"] <= 0) return 37;
                var artifactPath = System.IO.Path.Combine(AppPaths.Workspace, "artifact-test.txt"); File.WriteAllText(artifactPath, "artifact", new UTF8Encoding(false));
                store.RecordArtifact("task-live", "run-live", "test", artifactPath, "text/plain", new JObject());
                if (store.ListArtifacts("run-live").Count != 1) return 38;
                if (!Directory.GetDirectories(AppPaths.Data, "migration-backup-*").Any()) return 39;
                var scheduleId = "schedule-durable"; var due = DateTimeOffset.UtcNow.AddSeconds(-1);
                store.ReplaceSchedules(new JArray(new JObject { ["id"] = scheduleId, ["sessionId"] = "task-live", ["enabled"] = true, ["at"] = due.ToString("o"), ["text"] = "可靠调度", ["maxRetries"] = 1 }));
                if (store.ClaimDueSchedules("host:test", DateTimeOffset.UtcNow, 5).Count != 1) return 40;
                if (store.ClaimDueSchedules("host:other", DateTimeOffset.UtcNow, 5).Count != 0) return 41;
                store.CompleteSchedule(scheduleId, false, "first failure sk-secret-value", DateTimeOffset.UtcNow);
                store.DeferSchedule(scheduleId, DateTimeOffset.UtcNow.AddSeconds(-1), "retry-now");
                if (store.ClaimDueSchedules("host:test", DateTimeOffset.UtcNow, 5).Count != 1) return 42;
                store.CompleteSchedule(scheduleId, false, "second failure", DateTimeOffset.UtcNow);
                if ((string)store.ListSchedules(true).First["state"] != "dead_letter") return 43;
                if (!store.ReplayDeadLetter(scheduleId) || (string)store.ListSchedules(true).First["state"] != "scheduled") return 44;
                store.RecordFork("task-live", "task-fork", "checkpoint-one", new JObject { ["permissionInheritance"] = "restrict-only" });
                if (store.ListForks("task-live").Count != 1 || (string)store.ListForks("task-fork")[0]["parentTaskId"] != "task-live") return 45;
                return 0;
            }
            catch (Exception error)
            {
                try { File.WriteAllText(System.IO.Path.Combine(AppPaths.Data, "event-store-selftest-error.txt"), error.ToString(), new UTF8Encoding(false)); } catch { }
                return 39;
            }
        }
    }

    internal sealed class SqliteDb : IDisposable
    {
        private IntPtr _db;
        private SqliteDb(IntPtr db) { _db = db; Native.sqlite3_busy_timeout(_db, 5000); }
        public static SqliteDb Open(string path)
        {
            IntPtr db;
            var code = Native.sqlite3_open_v2(Utf8(path), out db, 0x00000002 | 0x00000004 | 0x00010000, null);
            if (code != 0) throw new InvalidOperationException("SQLite open failed: " + Error(db));
            return new SqliteDb(db);
        }
        public void Execute(string sql, params object[] values)
        {
            if (values == null || values.Length == 0)
            {
                IntPtr error;
                var result = Native.sqlite3_exec(_db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out error);
                if (result != 0)
                {
                    var message = error == IntPtr.Zero ? Error(_db) : Ptr(error);
                    if (error != IntPtr.Zero) Native.sqlite3_free(error);
                    throw new InvalidOperationException("SQLite execute failed: " + message + " SQL=" + Limit(sql));
                }
                return;
            }
            using (var statement = Prepare(sql))
            {
                statement.Bind(values);
                var code = Native.sqlite3_step(statement.Handle);
                if (code != 101 && code != 100) throw new InvalidOperationException("SQLite execute failed: " + Error(_db) + " SQL=" + Limit(sql));
                while (code == 100) code = Native.sqlite3_step(statement.Handle);
                if (code != 101) throw new InvalidOperationException("SQLite execute failed: " + Error(_db));
            }
        }
        public List<Dictionary<string, object>> Query(string sql, params object[] values)
        {
            using (var statement = Prepare(sql))
            {
                statement.Bind(values); var result = new List<Dictionary<string, object>>(); int code;
                while ((code = Native.sqlite3_step(statement.Handle)) == 100)
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    var count = Native.sqlite3_column_count(statement.Handle);
                    for (var index = 0; index < count; index++) row[Ptr(Native.sqlite3_column_name(statement.Handle, index))] = statement.Column(index);
                    result.Add(row);
                }
                if (code != 101) throw new InvalidOperationException("SQLite query failed: " + Error(_db));
                return result;
            }
        }
        public long ScalarInt64(string sql, params object[] values) { var rows = Query(sql, values); return rows.Count == 0 || rows[0].Count == 0 ? 0L : Convert.ToInt64(rows[0].Values.First()); }
        public string ScalarString(string sql, params object[] values) { var rows = Query(sql, values); return rows.Count == 0 || rows[0].Count == 0 || rows[0].Values.First() == null ? null : Convert.ToString(rows[0].Values.First()); }
        private SqliteStatement Prepare(string sql)
        {
            IntPtr statement;
            var code = Native.sqlite3_prepare_v2(_db, Utf8(sql), -1, out statement, IntPtr.Zero);
            if (code != 0) throw new InvalidOperationException("SQLite prepare failed: " + Error(_db) + " SQL=" + Limit(sql));
            return new SqliteStatement(statement);
        }
        public void Dispose() { if (_db == IntPtr.Zero) return; Native.sqlite3_close_v2(_db); _db = IntPtr.Zero; }
        private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes((value ?? "") + "\0"); }
        internal static string Ptr(IntPtr pointer, int length = -1) { if (pointer == IntPtr.Zero) return null; if (length < 0) { length = 0; while (Marshal.ReadByte(pointer, length) != 0) length++; } var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return Encoding.UTF8.GetString(bytes); }
        private static string Error(IntPtr db) { return db == IntPtr.Zero ? "unknown" : Ptr(Native.sqlite3_errmsg(db)); }
        private static string Limit(string sql) { return sql == null ? "" : sql.Length <= 180 ? sql : sql.Substring(0, 180); }
        internal static class Native
        {
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, byte[] vfs);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int ms);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern void sqlite3_free(IntPtr value);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr statement);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_name(IntPtr statement, int index);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr statement, int index);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int index);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern double sqlite3_column_double(IntPtr statement, int index);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
            [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int index);
        }
    }

    internal sealed class SqliteStatement : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public SqliteStatement(IntPtr handle) { Handle = handle; }
        public void Bind(object[] values)
        {
            for (var index = 0; index < (values == null ? 0 : values.Length); index++)
            {
                var value = values[index]; var parameter = index + 1; int code;
                if (value == null || value is DBNull) code = SqliteDb.Native.sqlite3_bind_null(Handle, parameter);
                else if (value is bool) code = SqliteDb.Native.sqlite3_bind_int64(Handle, parameter, (bool)value ? 1 : 0);
                else if (value is byte || value is short || value is int || value is long) code = SqliteDb.Native.sqlite3_bind_int64(Handle, parameter, Convert.ToInt64(value));
                else if (value is float || value is double || value is decimal) code = SqliteDb.Native.sqlite3_bind_double(Handle, parameter, Convert.ToDouble(value));
                else { var bytes = Encoding.UTF8.GetBytes(Convert.ToString(value)); code = SqliteDb.Native.sqlite3_bind_text(Handle, parameter, bytes, bytes.Length, new IntPtr(-1)); }
                if (code != 0) throw new InvalidOperationException("SQLite bind failed: " + code);
            }
        }
        public object Column(int index)
        {
            var type = SqliteDb.Native.sqlite3_column_type(Handle, index);
            if (type == 1) return SqliteDb.Native.sqlite3_column_int64(Handle, index);
            if (type == 2) return SqliteDb.Native.sqlite3_column_double(Handle, index);
            if (type == 3) return SqliteDb.Ptr(SqliteDb.Native.sqlite3_column_text(Handle, index), SqliteDb.Native.sqlite3_column_bytes(Handle, index));
            return null;
        }
        public void Dispose() { if (Handle == IntPtr.Zero) return; SqliteDb.Native.sqlite3_finalize(Handle); Handle = IntPtr.Zero; }
    }
}

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
    internal sealed partial class AgentEventStore
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
CREATE TABLE IF NOT EXISTS tool_calls(id TEXT PRIMARY KEY,run_id TEXT NOT NULL,task_id TEXT,tool_name TEXT NOT NULL,state TEXT NOT NULL,input_json TEXT NOT NULL,output_json TEXT,error_text TEXT,input_meta_json TEXT NOT NULL DEFAULT '{}',output_meta_json TEXT NOT NULL DEFAULT '{}',terminal_reason TEXT,duration_ms INTEGER,started_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_tool_calls_run_updated ON tool_calls(run_id,updated_at);
CREATE TABLE IF NOT EXISTS schedules(id TEXT PRIMARY KEY,task_id TEXT,state TEXT NOT NULL,due_at TEXT,repeat_minutes INTEGER NOT NULL DEFAULT 0,conflict_policy TEXT,request_id TEXT UNIQUE,payload_json TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,retry_count INTEGER NOT NULL DEFAULT 0,max_retries INTEGER NOT NULL DEFAULT 3,lease_until TEXT,claimed_by TEXT,last_error TEXT,dead_lettered_at TEXT,last_run_at TEXT,active_run_id TEXT,last_run_id TEXT);
CREATE TABLE IF NOT EXISTS task_queue(id TEXT PRIMARY KEY,task_id TEXT NOT NULL,ordinal INTEGER NOT NULL,kind TEXT NOT NULL,state TEXT NOT NULL,prompt TEXT NOT NULL,request_id TEXT NOT NULL UNIQUE,run_id TEXT,input_offset INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_task_queue_task_state_order ON task_queue(task_id,state,ordinal,created_at);
CREATE TABLE IF NOT EXISTS artifacts(id TEXT PRIMARY KEY,task_id TEXT,run_id TEXT,kind TEXT NOT NULL,path TEXT,mime_type TEXT,size_bytes INTEGER,sha256 TEXT,metadata_json TEXT,created_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_artifacts_run ON artifacts(run_id,created_at);
CREATE TABLE IF NOT EXISTS context_entries(id TEXT PRIMARY KEY,run_id TEXT NOT NULL,task_id TEXT,source_type TEXT NOT NULL,label TEXT,estimated_tokens INTEGER NOT NULL,content_sha256 TEXT,metadata_json TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_context_entries_run ON context_entries(run_id,created_at);
CREATE TABLE IF NOT EXISTS workspace_memories(id TEXT PRIMARY KEY,workspace_path TEXT NOT NULL,title TEXT NOT NULL,content TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'active',source TEXT NOT NULL DEFAULT 'manual',source_run_id TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_workspace_memories_scope ON workspace_memories(workspace_path,state,updated_at DESC);
CREATE TABLE IF NOT EXISTS provider_health(provider_id TEXT NOT NULL,model_id TEXT NOT NULL,state TEXT NOT NULL,consecutive_failures INTEGER NOT NULL DEFAULT 0,success_count INTEGER NOT NULL DEFAULT 0,failure_count INTEGER NOT NULL DEFAULT 0,probe_success_count INTEGER NOT NULL DEFAULT 0,probe_failure_count INTEGER NOT NULL DEFAULT 0,last_latency_ms INTEGER,last_success_at TEXT,last_failure_at TEXT,last_probe_at TEXT,cooldown_until TEXT,failure_kind TEXT,last_error TEXT,evidence TEXT,updated_at TEXT NOT NULL,PRIMARY KEY(provider_id,model_id));
CREATE INDEX IF NOT EXISTS ix_provider_health_state ON provider_health(state,cooldown_until,updated_at DESC);
CREATE TABLE IF NOT EXISTS task_forks(id TEXT PRIMARY KEY,parent_task_id TEXT NOT NULL,child_task_id TEXT NOT NULL,checkpoint_id TEXT,created_at TEXT NOT NULL,metadata_json TEXT NOT NULL,UNIQUE(child_task_id));
CREATE INDEX IF NOT EXISTS ix_task_forks_parent ON task_forks(parent_task_id,created_at);
CREATE TABLE IF NOT EXISTS agent_children(id TEXT PRIMARY KEY,parent_run_id TEXT NOT NULL,child_run_id TEXT NOT NULL UNIQUE,task_id TEXT NOT NULL,state TEXT NOT NULL,depth INTEGER NOT NULL DEFAULT 1,handoff_state TEXT NOT NULL DEFAULT 'ready',attempt INTEGER NOT NULL DEFAULT 1,max_retries INTEGER NOT NULL DEFAULT 2,retry_of TEXT,dependency_json TEXT NOT NULL DEFAULT '[]',result_json TEXT NOT NULL DEFAULT '{}',error_text TEXT NOT NULL DEFAULT '',metadata_json TEXT NOT NULL DEFAULT '{}',created_at TEXT NOT NULL,updated_at TEXT NOT NULL,finished_at TEXT);
CREATE INDEX IF NOT EXISTS ix_agent_children_parent ON agent_children(parent_run_id,created_at);
CREATE INDEX IF NOT EXISTS ix_agent_children_retry ON agent_children(parent_run_id,retry_of,attempt);
CREATE TABLE IF NOT EXISTS snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT,aggregate_type TEXT NOT NULL,aggregate_id TEXT NOT NULL,event_seq INTEGER NOT NULL,state_json TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_snapshots_aggregate ON snapshots(aggregate_type,aggregate_id,event_seq DESC);
CREATE TABLE IF NOT EXISTS migration_sources(path TEXT PRIMARY KEY,size_bytes INTEGER NOT NULL,modified_utc TEXT NOT NULL,sha256 TEXT NOT NULL,imported_at TEXT NOT NULL);
INSERT OR REPLACE INTO schema_meta(key,value) VALUES('schema_version','11');
");
                if (!db.Query("PRAGMA table_info(task_queue)").Any(row => string.Equals(Convert.ToString(row["name"]), "input_offset", StringComparison.OrdinalIgnoreCase)))
                    db.Execute("ALTER TABLE task_queue ADD COLUMN input_offset INTEGER NOT NULL DEFAULT 0");
                EnsureScheduleColumns(db);
                EnsureToolCallColumns(db);
                EnsureAgentChildColumns(db);
                if (db.ScalarString("SELECT value FROM schema_meta WHERE key='legacy_migration_complete'") != "1") MigrateLegacy(db);
            }
        }

        private static void EnsureToolCallColumns(SqliteDb db)
        {
            var columns = new HashSet<string>(db.Query("PRAGMA table_info(tool_calls)").Select(row => Convert.ToString(row["name"])), StringComparer.OrdinalIgnoreCase);
            var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "input_meta_json", "TEXT NOT NULL DEFAULT '{}'" }, { "output_meta_json", "TEXT NOT NULL DEFAULT '{}'" },
                { "terminal_reason", "TEXT" }, { "duration_ms", "INTEGER" }
            };
            foreach (var pair in additions) if (!columns.Contains(pair.Key)) db.Execute("ALTER TABLE tool_calls ADD COLUMN " + pair.Key + " " + pair.Value);
        }

        private static void EnsureAgentChildColumns(SqliteDb db)
        {
            var columns = new HashSet<string>(db.Query("PRAGMA table_info(agent_children)").Select(row => Convert.ToString(row["name"])), StringComparer.OrdinalIgnoreCase);
            var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "attempt", "INTEGER NOT NULL DEFAULT 1" }, { "max_retries", "INTEGER NOT NULL DEFAULT 2" }, { "retry_of", "TEXT" }, { "dependency_json", "TEXT NOT NULL DEFAULT '[]'" }
            };
            foreach (var pair in additions) if (!columns.Contains(pair.Key)) db.Execute("ALTER TABLE agent_children ADD COLUMN " + pair.Key + " " + pair.Value);
        }

        private static void EnsureScheduleColumns(SqliteDb db)
        {
            var columns = new HashSet<string>(db.Query("PRAGMA table_info(schedules)").Select(row => Convert.ToString(row["name"])), StringComparer.OrdinalIgnoreCase);
            var additions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "retry_count", "INTEGER NOT NULL DEFAULT 0" }, { "max_retries", "INTEGER NOT NULL DEFAULT 3" },
                { "lease_until", "TEXT" }, { "claimed_by", "TEXT" }, { "last_error", "TEXT" },
                { "dead_lettered_at", "TEXT" }, { "last_run_at", "TEXT" }, { "active_run_id", "TEXT" }, { "last_run_id", "TEXT" }
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
                catch (Exception error) { CrashLog.Handled("EventStoreBackup", error); }
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
                db.Execute("UPDATE agent_children SET state=?2,updated_at=?3 WHERE child_run_id=?1 AND state NOT IN ('completed','failed','cancelled')", id, state, now);
                UpdateTaskStateForRun(db, id, state, now);
            }
        }

        public void UpdateRunState(string id, string state, int workerPid, string details)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var now = ProviderStore.NowIso();
                db.Execute("UPDATE runs SET state=?2,worker_pid=?3,lease_until=?4,updated_at=?5,source_json=?6 WHERE id=?1", id, state, workerPid,
                    DateTimeOffset.Now.AddSeconds(8).ToString("o"), now, details ?? "{}");
                db.Execute("UPDATE agent_children SET state=?2,updated_at=?3 WHERE child_run_id=?1 AND state NOT IN ('completed','failed','cancelled')", id, state, now);
                UpdateTaskStateForRun(db, id, state, now);
                if (state == JobStates.Completed || state == JobStates.Failed || state == JobStates.Cancelled)
                {
                    var child = db.Query("SELECT id FROM agent_children WHERE child_run_id=?1", id).FirstOrDefault();
                    if (child != null)
                    {
                        var error = ""; var result = details ?? "{}";
                        try
                        {
                            var parsed = JObject.Parse(result);
                            error = (string)parsed["result"] ?? (string)parsed["message"] ?? (string)parsed["error"] ?? "";
                        }
                        catch { error = result; }
                        bool redacted; error = ToolRuntimePolicy.RedactAndLimit(error, 65536, out redacted);
                        db.Execute(@"UPDATE agent_children SET state=?2,handoff_state='ready',
result_json=CASE WHEN result_json IS NULL OR result_json='' OR result_json='{}' THEN ?3 ELSE result_json END,
error_text=CASE WHEN error_text IS NULL OR error_text='' THEN ?4 ELSE error_text END,
updated_at=?5,finished_at=COALESCE(finished_at,?5) WHERE child_run_id=?1", id, state, result, state == JobStates.Completed ? "" : error, now);
                    }
                }
            }
        }

        public string TaskState(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return db.ScalarString("SELECT state FROM tasks WHERE id=?1", id) ?? "";
        }

        private static void UpdateTaskStateForRun(SqliteDb db, string runId, string requestedState, string now)
        {
            var run = db.Query("SELECT task_id FROM runs WHERE id=?1", runId).FirstOrDefault();
            if (run == null) return;
            var taskId = Convert.ToString(run["task_id"]);
            if (string.IsNullOrWhiteSpace(taskId)) return;
            var state = requestedState ?? JobStates.Running;
            if (state == JobStates.Completed || state == JobStates.Failed || state == JobStates.Cancelled)
            {
                var active = db.Query(@"SELECT state FROM runs WHERE task_id=?1 AND id<>?2
AND state IN ('queued','starting','running','waiting','waiting_approval','waiting_user','paused')
ORDER BY updated_at DESC LIMIT 1", taskId, runId).FirstOrDefault();
                if (active != null) state = Convert.ToString(active["state"]);
            }
            db.Execute("UPDATE tasks SET state=?2,updated_at=?3 WHERE id=?1", taskId, state, now);
        }

        public JObject CreateChildAgent(string parentRunId, string childRunId, string taskId, int depth, JObject metadata)
        {
            parentRunId = parentRunId ?? ""; childRunId = childRunId ?? ""; taskId = taskId ?? "";
            var now = ProviderStore.NowIso(); var id = "child:" + childRunId;
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var parent = db.Query("SELECT id FROM runs WHERE id=?1", parentRunId);
                if (parent.Count == 0) throw new InvalidOperationException("Parent Run does not exist.");
                var active = db.ScalarInt64("SELECT COUNT(*) FROM agent_children WHERE parent_run_id=?1 AND state IN ('starting','running','waiting','paused')", parentRunId);
                if (active >= 4) throw new InvalidOperationException("Parent Agent has reached its child concurrency limit.");
                metadata = metadata ?? new JObject();
                var attempt = Math.Max(1, (int?)metadata["attempt"] ?? 1); var maxRetries = Math.Max(0, Math.Min(10, (int?)metadata["maxRetries"] ?? 2));
                var retryOf = (string)metadata["retryOf"] ?? ""; var dependencies = metadata["dependencies"] as JArray ?? new JArray();
                foreach (var dependency in dependencies.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    var dependencyState = db.ScalarString("SELECT state FROM agent_children WHERE (id=?1 OR child_run_id=?1) AND parent_run_id=?2", dependency, parentRunId);
                    if (!string.Equals(dependencyState, JobStates.Completed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Child Agent dependency is not completed: " + dependency);
                }
                db.Execute("INSERT INTO agent_children(id,parent_run_id,child_run_id,task_id,state,depth,handoff_state,attempt,max_retries,retry_of,dependency_json,result_json,error_text,metadata_json,created_at,updated_at) VALUES(?1,?2,?3,?4,'starting',?5,'pending',?6,?7,NULLIF(?8,''),?9,'{}','',?10,?11,?11)",
                    id, parentRunId, childRunId, taskId, Math.Max(1, depth), attempt, maxRetries, retryOf, dependencies.ToString(Formatting.None), metadata.ToString(Formatting.None), now);
            }
            return new JObject { ["id"] = id, ["parentRunId"] = parentRunId, ["childRunId"] = childRunId, ["taskId"] = taskId, ["state"] = "starting", ["depth"] = depth, ["attempt"] = (int?)metadata?["attempt"] ?? 1, ["maxRetries"] = (int?)metadata?["maxRetries"] ?? 2, ["retryOf"] = metadata?["retryOf"], ["handoffState"] = "pending", ["createdAt"] = now };
        }

        public JObject ChildAgent(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM agent_children WHERE id=?1 OR child_run_id=?1", id ?? "");
                return rows.Count == 0 ? null : ChildAgentRow(rows[0]);
            }
        }

        public JArray ListChildAgents(string parentRunId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT * FROM agent_children WHERE parent_run_id=?1 ORDER BY created_at", parentRunId ?? "").Select(ChildAgentRow));
        }

        public JObject AcceptChildHandoff(string id, string parentRunId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM agent_children WHERE (id=?1 OR child_run_id=?1) AND parent_run_id=?2", id ?? "", parentRunId ?? "");
                if (rows.Count == 0) return null;
                var row = rows[0]; var childId = Convert.ToString(row["child_run_id"]); var now = ProviderStore.NowIso();
                if (Convert.ToString(row["state"]) != JobStates.Completed) throw new InvalidOperationException("Only a completed child Agent can be handed off.");
                db.Execute("UPDATE agent_children SET handoff_state='accepted',updated_at=?2 WHERE child_run_id=?1", childId, now);
                JObject result = ChildAgentRow(row); result["handoffState"] = "accepted"; return result;
            }
        }

        public JObject PrepareChildRetry(string id, string parentRunId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM agent_children WHERE (id=?1 OR child_run_id=?1) AND parent_run_id=?2", id ?? "", parentRunId ?? "");
                if (rows.Count == 0) return null;
                var row = rows[0]; var state = Convert.ToString(row["state"]); if (state != JobStates.Failed && state != JobStates.Cancelled) throw new InvalidOperationException("Only a failed or cancelled child Agent can be retried.");
                var attempt = Convert.ToInt32(row["attempt"]); var maxRetries = Convert.ToInt32(row["max_retries"]); if (attempt > maxRetries) throw new InvalidOperationException("Child Agent retry limit has been reached.");
                var request = JsonUtil.Read(System.IO.Path.Combine(AppPaths.Runs, Convert.ToString(row["child_run_id"]), "request.json"), new JObject()) as JObject ?? new JObject();
                if (request.Count == 0) throw new InvalidOperationException("The failed child request is unavailable.");
                return new JObject
                {
                    ["parentRunId"] = parentRunId, ["prompt"] = request["prompt"], ["childName"] = request["childName"],
                    ["retryOf"] = Convert.ToString(row["child_run_id"]), ["attempt"] = attempt + 1, ["maxRetries"] = maxRetries,
                    ["dependencies"] = ParseJson(Convert.ToString(row["dependency_json"])) ?? new JArray(), ["sourceChild"] = ChildAgentRow(row)
                };
            }
        }

        private static JObject ChildAgentRow(Dictionary<string, object> row)
        {
            return new JObject
            {
                ["id"] = Convert.ToString(row["id"]), ["parentRunId"] = Convert.ToString(row["parent_run_id"]), ["childRunId"] = Convert.ToString(row["child_run_id"]),
                ["taskId"] = Convert.ToString(row["task_id"]), ["state"] = Convert.ToString(row["state"]), ["depth"] = Convert.ToInt32(row["depth"]),
                ["handoffState"] = Convert.ToString(row["handoff_state"]), ["attempt"] = row.ContainsKey("attempt") ? Convert.ToInt32(row["attempt"]) : 1, ["maxRetries"] = row.ContainsKey("max_retries") ? Convert.ToInt32(row["max_retries"]) : 2,
                ["retryOf"] = Convert.ToString(row.ContainsKey("retry_of") ? row["retry_of"] : null), ["dependencies"] = ParseJson(Convert.ToString(row.ContainsKey("dependency_json") ? row["dependency_json"] : "[]")),
                ["result"] = ParseJson(Convert.ToString(row["result_json"])), ["error"] = Convert.ToString(row["error_text"]),
                ["metadata"] = ParseJson(Convert.ToString(row["metadata_json"])), ["createdAt"] = Convert.ToString(row["created_at"]), ["updatedAt"] = Convert.ToString(row["updated_at"]), ["finishedAt"] = Convert.ToString(row["finished_at"])
            };
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

        public JArray ListOpenApprovals()
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query(
                "SELECT id,run_id,tool_name,state,input_json,decision_json,created_at,updated_at FROM approvals WHERE state IN ('pending','allow','deny') ORDER BY created_at")
                .Select(row => new JObject
                {
                    ["id"] = Convert.ToString(row["id"]), ["runId"] = Convert.ToString(row["run_id"]),
                    ["toolName"] = Convert.ToString(row["tool_name"]), ["state"] = Convert.ToString(row["state"]),
                    ["input"] = ParseJson(Convert.ToString(row["input_json"])) ?? new JObject(),
                    ["decision"] = ParseJson(Convert.ToString(row["decision_json"])) ?? new JObject(),
                    ["createdAt"] = Convert.ToString(row["created_at"]), ["updatedAt"] = Convert.ToString(row["updated_at"])
                }));
        }

        public void CloseApproval(string id, string state)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
                db.Execute("UPDATE approvals SET state=?2,updated_at=?3 WHERE id=?1", id ?? "", state ?? "consumed", ProviderStore.NowIso());
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
                    db.Execute("UPDATE schedules SET state='retry',claimed_by=NULL,lease_until=NULL,active_run_id=NULL,updated_at=?1,last_error=CASE WHEN COALESCE(last_error,'')='' THEN 'Host lease expired; execution recovered' ELSE last_error END WHERE state='running' AND lease_until IS NOT NULL AND lease_until<?1", stamp);
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

        public JObject BindScheduleRun(string id, string runId, string owner, DateTimeOffset now)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var stamp = now.ToUniversalTime().ToString("o"); var lease = now.AddMinutes(2).ToUniversalTime().ToString("o");
                db.Execute("UPDATE schedules SET state='running',active_run_id=?2,last_run_id=?2,claimed_by=?3,lease_until=?4,last_run_at=?5,updated_at=?5 WHERE id=?1 AND state='running'", id ?? "", runId ?? "", owner ?? "", lease, stamp);
                var rows = db.Query("SELECT * FROM schedules WHERE id=?1", id ?? ""); return rows.Count == 0 ? null : ScheduleItem(rows[0]);
            }
        }

        public JObject RenewScheduleLease(string id, string owner, DateTimeOffset now)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var stamp = now.ToUniversalTime().ToString("o"); var lease = now.AddMinutes(2).ToUniversalTime().ToString("o");
                db.Execute("UPDATE schedules SET claimed_by=?2,lease_until=?3,updated_at=?4 WHERE id=?1 AND state='running'", id ?? "", owner ?? "", lease, stamp);
                var rows = db.Query("SELECT * FROM schedules WHERE id=?1", id ?? ""); return rows.Count == 0 ? null : ScheduleItem(rows[0]);
            }
        }

        public JArray ListRunningSchedules()
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT * FROM schedules WHERE state='running' ORDER BY updated_at").Select(ScheduleItem));
        }

        public JObject GetRunSnapshot(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT id,state,worker_pid,source_json,updated_at FROM runs WHERE id=?1", id ?? "");
                if (rows.Count == 0) return null; var row = rows[0];
                return new JObject { ["id"] = Convert.ToString(row["id"]), ["state"] = Convert.ToString(row["state"]), ["workerPid"] = Convert.ToInt32(row["worker_pid"]), ["details"] = ParseJson(Convert.ToString(row["source_json"])), ["updatedAt"] = Convert.ToString(row["updated_at"]) };
            }
        }

        public JObject CompleteSchedule(string id, bool success, string error, DateTimeOffset now)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM schedules WHERE id=?1", id); if (rows.Count == 0) return null;
                var row = rows[0]; var repeat = Convert.ToInt32(row["repeat_minutes"]); var retries = Convert.ToInt32(row["retry_count"]); var max = Convert.ToInt32(row["max_retries"]);
                var stamp = now.ToUniversalTime().ToString("o");
                if (success)
                {
                    var state = repeat > 0 ? "scheduled" : "completed"; var due = repeat > 0 ? now.AddMinutes(repeat).ToUniversalTime().ToString("o") : Convert.ToString(row["due_at"]);
                    db.Execute("UPDATE schedules SET state=?2,due_at=?3,retry_count=0,lease_until=NULL,claimed_by=NULL,active_run_id=NULL,last_error=NULL,last_run_at=?4,updated_at=?4 WHERE id=?1", id, state, due, stamp);
                }
                else if (retries < max)
                {
                    var nextRetry = retries + 1; var delaySeconds = Math.Min(1800, 15 * (1 << Math.Min(nextRetry - 1, 7)));
                    db.Execute("UPDATE schedules SET state='retry',due_at=?2,retry_count=?3,lease_until=NULL,claimed_by=NULL,active_run_id=NULL,last_error=?4,last_run_at=?5,updated_at=?5 WHERE id=?1", id, now.AddSeconds(delaySeconds).ToUniversalTime().ToString("o"), nextRetry, ScheduleLimit(error, 2000), stamp);
                }
                else db.Execute("UPDATE schedules SET state='dead_letter',lease_until=NULL,claimed_by=NULL,active_run_id=NULL,last_error=?2,dead_lettered_at=?3,last_run_at=?3,updated_at=?3 WHERE id=?1", id, ScheduleLimit(error, 2000), stamp);
                return ScheduleItem(db.Query("SELECT * FROM schedules WHERE id=?1", id).First());
            }
        }

        public void DeferSchedule(string id, DateTimeOffset due, string reason)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("UPDATE schedules SET state='scheduled',due_at=?2,lease_until=NULL,claimed_by=NULL,active_run_id=NULL,last_error=?3,updated_at=?4 WHERE id=?1", id, due.ToUniversalTime().ToString("o"), ScheduleLimit(reason, 500), ProviderStore.NowIso());
        }

        public bool ReplayDeadLetter(string id)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("UPDATE schedules SET state='scheduled',due_at=?2,retry_count=0,lease_until=NULL,claimed_by=NULL,active_run_id=NULL,last_error=NULL,dead_lettered_at=NULL,updated_at=?2 WHERE id=?1 AND state='dead_letter'", id, ProviderStore.NowIso());
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
            payload["activeRunId"] = Convert.ToString(row.ContainsKey("active_run_id") ? row["active_run_id"] : null); payload["lastRunId"] = Convert.ToString(row.ContainsKey("last_run_id") ? row["last_run_id"] : null);
            return payload;
        }

        private static string SafeIdentifier(string value) { return new string((value ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').Take(120).ToArray()); }
        private static string ScheduleLimit(string value, int max) { bool ignored; return ToolRuntimePolicy.RedactAndLimit(value ?? "", max, out ignored); }

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

        public long AppendWorkerEvent(string runId, string taskId, string payload, string eventId = null, JObject toolRuntimePolicy = null)
        {
            return AppendWorkerEvents(runId, taskId,
                new[] { new KeyValuePair<string, string>(eventId, payload) }, toolRuntimePolicy);
        }

        public long AppendWorkerEvents(string runId, string taskId, IEnumerable<KeyValuePair<string, string>> events, JObject toolRuntimePolicy = null)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    var now = ProviderStore.NowIso();
                    long next = 0;
                    foreach (var item in events ?? Enumerable.Empty<KeyValuePair<string, string>>())
                    {
                        var stableId = string.IsNullOrWhiteSpace(item.Key) ? "native:" + runId + ":" + Guid.NewGuid().ToString("N") : item.Key;
                        var payload = item.Value ?? "";
                        var persistedPayload = ToolRuntimePolicy.SanitizeWorkerEvent(payload, toolRuntimePolicy);
                        next = AppendEvent(db, runId, taskId, null, "claude-stream", persistedPayload, stableId, now);
                        NormalizeToolCalls(db, runId, taskId, payload, now, toolRuntimePolicy);
                    }
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
            lock (_gate) using (var db = SqliteDb.Open(_path)) return new JArray(db.Query("SELECT id,tool_name,state,input_json,output_json,error_text,input_meta_json,output_meta_json,terminal_reason,duration_ms,started_at,updated_at FROM tool_calls WHERE run_id=?1 ORDER BY started_at", runId).Select(row => new JObject
            {
                ["id"] = Convert.ToString(row["id"]), ["toolName"] = Convert.ToString(row["tool_name"]), ["state"] = Convert.ToString(row["state"]),
                ["input"] = ParseJson(Convert.ToString(row["input_json"])), ["output"] = ParseJson(Convert.ToString(row["output_json"])),
                ["error"] = Convert.ToString(row["error_text"]), ["inputPersistence"] = ParseJson(Convert.ToString(row["input_meta_json"])),
                ["outputPersistence"] = ParseJson(Convert.ToString(row["output_meta_json"])), ["terminalReason"] = Convert.ToString(row["terminal_reason"]),
                ["durationMs"] = row["duration_ms"] == null ? 0L : Convert.ToInt64(row["duration_ms"]),
                ["startedAt"] = Convert.ToString(row["started_at"]), ["updatedAt"] = Convert.ToString(row["updated_at"])
            }));
        }

        public int TransitionActiveToolCalls(string runId, string state, string reason)
        {
            var allowed = new[] { "failed", "cancelled", "timed_out" };
            if (!allowed.Contains(state, StringComparer.Ordinal)) throw new ArgumentException("Unsupported terminal ToolCall state.", "state");
            bool ignored; reason = ToolRuntimePolicy.RedactAndLimit(reason ?? "", 32768, out ignored);
            var now = ProviderStore.NowIso();
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute(@"UPDATE tool_calls SET state=?2,error_text=?3,terminal_reason=?3,
duration_ms=MAX(0,CAST((julianday(?4)-julianday(started_at))*86400000 AS INTEGER)),updated_at=?4 WHERE run_id=?1 AND state='running'", runId, state, reason, now);
                return (int)db.ScalarInt64("SELECT changes()");
            }
        }

        public JArray MarkTimedOutToolCalls(string runId, int maxDurationSeconds)
        {
            var now = DateTimeOffset.Now; var cutoff = now.AddSeconds(-Math.Max(0, maxDurationSeconds)); var result = new JArray();
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                foreach (var row in db.Query("SELECT id,tool_name,started_at FROM tool_calls WHERE run_id=?1 AND state='running' ORDER BY started_at", runId))
                {
                    DateTimeOffset started;
                    if (!DateTimeOffset.TryParse(Convert.ToString(row["started_at"]), out started) || started > cutoff) continue;
                    var id = Convert.ToString(row["id"]); var elapsed = Math.Max(0L, (long)(now - started).TotalMilliseconds);
                    var reason = "Tool exceeded the " + maxDurationSeconds + " second runtime limit.";
                    db.Execute("UPDATE tool_calls SET state='timed_out',error_text=?2,terminal_reason=?2,duration_ms=?3,updated_at=?4 WHERE id=?1 AND state='running'", id, reason, elapsed, now.ToString("o"));
                    if (db.ScalarInt64("SELECT changes()") == 1) result.Add(new JObject { ["id"] = id, ["toolName"] = Convert.ToString(row["tool_name"]), ["durationMs"] = elapsed, ["reason"] = reason });
                }
            }
            return result;
        }

        public void RecordContext(string runId, string taskId, string sourceType, string label, string content, JObject metadata)
        {
            content = content ?? ""; var hash = Sha256Text(content);
            var identity = ((string)metadata?["memoryId"] ?? "").Trim();
            var identityHash = identity.Length == 0 ? hash : Sha256Text(identity);
            var id = "context:" + runId + ":" + sourceType + ":" + identityHash.Substring(0, 16);
            var estimatedTokens = Math.Max(0L, (long?)metadata?["estimatedTokens"] ?? EstimateTokens(content));
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("INSERT OR REPLACE INTO context_entries(id,run_id,task_id,source_type,label,estimated_tokens,content_sha256,metadata_json,created_at) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)",
                id, runId, taskId, sourceType, label ?? "", estimatedTokens, hash, (metadata ?? new JObject()).ToString(Formatting.None), ProviderStore.NowIso());
        }

        public JObject ContextReport(string runId)
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT source_type,label,estimated_tokens,content_sha256,metadata_json,created_at FROM context_entries WHERE run_id=?1 ORDER BY created_at", runId);
                var sources = new JArray(rows.Select(row => new JObject { ["type"] = Convert.ToString(row["source_type"]), ["label"] = Convert.ToString(row["label"]), ["estimatedTokens"] = Convert.ToInt64(row["estimated_tokens"]), ["sha256"] = Convert.ToString(row["content_sha256"]), ["metadata"] = ParseJson(Convert.ToString(row["metadata_json"])), ["createdAt"] = Convert.ToString(row["created_at"]) }));
                var budget = sources.OfType<JObject>().FirstOrDefault(source => string.Equals((string)source["type"], "context-budget", StringComparison.Ordinal));
                return new JObject
                {
                    ["runId"] = runId, ["estimatedTokens"] = rows.Sum(row => Convert.ToInt64(row["estimated_tokens"])),
                    ["sources"] = sources,
                    ["budget"] = budget?["metadata"]?.DeepClone()
                };
            }
        }

        public JObject RecordProviderOutcome(string providerId, string model, bool success, string failureKind, string message, long latencyMs, int cooldownSeconds, string evidence)
        {
            return RecordProviderHealth(providerId, model, true, success, failureKind, message, latencyMs, cooldownSeconds, evidence);
        }

        public JObject RecordProviderProbe(string providerId, bool success, string failureKind, string message, long latencyMs, int cooldownSeconds, string evidence)
        {
            return RecordProviderHealth(providerId, "*", false, success, failureKind, message, latencyMs, cooldownSeconds, evidence);
        }

        private JObject RecordProviderHealth(string providerId, string model, bool execution, bool success, string failureKind, string message, long latencyMs, int cooldownSeconds, string evidence)
        {
            providerId = (providerId ?? "").Trim(); model = string.IsNullOrWhiteSpace(model) ? "*" : model.Trim();
            if (providerId.Length == 0) return new JObject();
            var now = DateTimeOffset.Now; var stamp = now.ToString("o");
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM provider_health WHERE provider_id=?1 AND model_id=?2", providerId, model);
                var row = rows.Count > 0 ? rows[0] : new Dictionary<string, object>();
                var consecutive = Value(row, "consecutive_failures");
                var successCount = Value(row, "success_count"); var failureCount = Value(row, "failure_count");
                var probeSuccess = Value(row, "probe_success_count"); var probeFailure = Value(row, "probe_failure_count");
                var previousState = Convert.ToString(row.ContainsKey("state") ? row["state"] : null);
                string state; string cooldown = null;
                if (execution)
                {
                    if (success) { consecutive = 0; successCount++; state = "healthy"; }
                    else
                    {
                        consecutive++; failureCount++;
                        var permanentlyUnavailable = string.Equals(failureKind, "model_unavailable", StringComparison.OrdinalIgnoreCase);
                        cooldown = permanentlyUnavailable ? null : cooldownSeconds > 0 ? now.AddSeconds(cooldownSeconds).ToString("o") : null;
                        state = permanentlyUnavailable ? "unavailable" : cooldown == null ? "degraded" : "cooling";
                    }
                }
                else
                {
                    if (success) probeSuccess++; else probeFailure++;
                    if (success) state = previousState == "healthy" ? "healthy" : "reachable";
                    else
                    {
                        cooldown = cooldownSeconds > 0 ? now.AddSeconds(cooldownSeconds).ToString("o") : Convert.ToString(row.ContainsKey("cooldown_until") ? row["cooldown_until"] : null);
                        state = previousState == "healthy" ? "degraded" : cooldown == null ? "probe_failed" : "cooling";
                    }
                }
                var safeMessage = SecretRedactor.Redact(message ?? "");
                if (safeMessage.Length > 1600) safeMessage = safeMessage.Substring(0, 1600);
                db.Execute(@"INSERT INTO provider_health(provider_id,model_id,state,consecutive_failures,success_count,failure_count,probe_success_count,probe_failure_count,last_latency_ms,last_success_at,last_failure_at,last_probe_at,cooldown_until,failure_kind,last_error,evidence,updated_at)
VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17)
ON CONFLICT(provider_id,model_id) DO UPDATE SET state=excluded.state,consecutive_failures=excluded.consecutive_failures,success_count=excluded.success_count,failure_count=excluded.failure_count,probe_success_count=excluded.probe_success_count,probe_failure_count=excluded.probe_failure_count,last_latency_ms=excluded.last_latency_ms,last_success_at=COALESCE(excluded.last_success_at,provider_health.last_success_at),last_failure_at=COALESCE(excluded.last_failure_at,provider_health.last_failure_at),last_probe_at=COALESCE(excluded.last_probe_at,provider_health.last_probe_at),cooldown_until=excluded.cooldown_until,failure_kind=excluded.failure_kind,last_error=excluded.last_error,evidence=excluded.evidence,updated_at=excluded.updated_at",
                    providerId, model, state, consecutive, successCount, failureCount, probeSuccess, probeFailure, Math.Max(0L, latencyMs),
                    execution && success ? stamp : null, execution && !success ? stamp : null, execution ? null : stamp, cooldown,
                    success ? "" : failureKind ?? "unknown", success ? "" : safeMessage, evidence ?? (execution ? "run-result" : "model-list"), stamp);
                return ProviderHealthItem(db.Query("SELECT * FROM provider_health WHERE provider_id=?1 AND model_id=?2", providerId, model).First(), now);
            }
        }

        public JArray ListProviderHealth(string providerId = "")
        {
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = string.IsNullOrWhiteSpace(providerId)
                    ? db.Query("SELECT * FROM provider_health ORDER BY provider_id,model_id")
                    : db.Query("SELECT * FROM provider_health WHERE provider_id=?1 ORDER BY model_id", providerId);
                var now = DateTimeOffset.Now;
                return new JArray(rows.Select(row => ProviderHealthItem(row, now)));
            }
        }

        public JObject GetProviderHealth(string providerId, string model)
        {
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(model)) return null;
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM provider_health WHERE provider_id=?1 AND model_id=?2", providerId.Trim(), model.Trim());
                return rows.Count == 0 ? null : ProviderHealthItem(rows[0], DateTimeOffset.Now);
            }
        }

        public void DeleteProviderHealth(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId)) return;
            lock (_gate) using (var db = SqliteDb.Open(_path)) db.Execute("DELETE FROM provider_health WHERE provider_id=?1", providerId);
        }

        public int PruneProviderHealth(IEnumerable<string> configuredProviderIds)
        {
            var allowed = new HashSet<string>((configuredProviderIds ?? Enumerable.Empty<string>())
                .Select(value => (value ?? "").Trim()).Where(value => value.Length > 0), StringComparer.Ordinal);
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var stale = db.Query("SELECT DISTINCT provider_id FROM provider_health")
                    .Select(row => Convert.ToString(row["provider_id"]))
                    .Where(value => !allowed.Contains(value)).ToArray();
                foreach (var providerId in stale) db.Execute("DELETE FROM provider_health WHERE provider_id=?1", providerId);
                return stale.Length;
            }
        }

        private static JObject ProviderHealthItem(Dictionary<string, object> row, DateTimeOffset now)
        {
            var state = Convert.ToString(row["state"]); var cooldownText = Convert.ToString(row["cooldown_until"]); DateTimeOffset cooldown;
            var cooling = DateTimeOffset.TryParse(cooldownText, out cooldown) && cooldown > now;
            if (state == "cooling" && !cooling) state = Value(row, "consecutive_failures") > 0 ? "degraded" : Value(row, "probe_failure_count") > 0 ? "probe_failed" : "unknown";
            return new JObject
            {
                ["providerId"] = Convert.ToString(row["provider_id"]), ["model"] = Convert.ToString(row["model_id"]), ["state"] = state,
                ["available"] = !cooling && state != "unavailable", ["consecutiveFailures"] = Value(row, "consecutive_failures"),
                ["successCount"] = Value(row, "success_count"), ["failureCount"] = Value(row, "failure_count"),
                ["probeSuccessCount"] = Value(row, "probe_success_count"), ["probeFailureCount"] = Value(row, "probe_failure_count"),
                ["lastLatencyMs"] = Value(row, "last_latency_ms"), ["lastSuccessAt"] = Convert.ToString(row["last_success_at"]),
                ["lastFailureAt"] = Convert.ToString(row["last_failure_at"]), ["lastProbeAt"] = Convert.ToString(row["last_probe_at"]),
                ["cooldownUntil"] = cooldownText, ["cooldownRemainingSeconds"] = cooling ? Math.Max(1, (long)Math.Ceiling((cooldown - now).TotalSeconds)) : 0L,
                ["failureKind"] = Convert.ToString(row["failure_kind"]), ["lastError"] = Convert.ToString(row["last_error"]),
                ["evidence"] = Convert.ToString(row["evidence"]), ["updatedAt"] = Convert.ToString(row["updated_at"])
            };
        }

        private static long Value(Dictionary<string, object> row, string key)
        {
            return row != null && row.ContainsKey(key) && row[key] != null ? Convert.ToInt64(row[key]) : 0L;
        }

        public JObject SaveWorkspaceMemory(string workspace, string id, string title, string content, bool active)
        {
            workspace = NormalizeWorkspace(workspace);
            title = (title ?? "").Trim();
            content = (content ?? "").Trim();
            if (title.Length == 0 || title.Length > 120) throw new InvalidOperationException("记忆标题必须为 1–120 个字符");
            if (content.Length == 0 || content.Length > 8000) throw new InvalidOperationException("记忆正文必须为 1–8,000 个字符");
            Guid parsed;
            if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString();
            else if (!Guid.TryParse(id, out parsed)) throw new InvalidOperationException("记忆 ID 无效");
            var state = active ? "active" : "inactive";
            var now = ProviderStore.NowIso();
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                if (active) ValidateMemoryCapacity(db, workspace, id, content.Length);
                db.Execute(@"INSERT INTO workspace_memories(id,workspace_path,title,content,state,source,created_at,updated_at)
VALUES(?1,?2,?3,?4,?5,'manual',?6,?6)
ON CONFLICT(id) DO UPDATE SET workspace_path=excluded.workspace_path,title=excluded.title,content=excluded.content,state=excluded.state,updated_at=excluded.updated_at",
                    id, workspace, title, content, state, now);
                return MemoryItem(db.Query("SELECT * FROM workspace_memories WHERE id=?1", id).First());
            }
        }

        public JArray ListWorkspaceMemories(string workspace, bool includeInactive)
        {
            workspace = NormalizeWorkspace(workspace);
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = includeInactive
                    ? db.Query("SELECT * FROM workspace_memories WHERE workspace_path=?1 ORDER BY CASE state WHEN 'active' THEN 0 ELSE 1 END,updated_at DESC", workspace)
                    : db.Query("SELECT * FROM workspace_memories WHERE workspace_path=?1 AND state='active' ORDER BY updated_at DESC", workspace);
                return new JArray(rows.Select(MemoryItem));
            }
        }

        public JObject SetWorkspaceMemoryActive(string workspace, string id, bool active)
        {
            workspace = NormalizeWorkspace(workspace);
            Guid parsed;
            if (!Guid.TryParse((id ?? "").Trim(), out parsed)) throw new InvalidOperationException("记忆 ID 无效");
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                var rows = db.Query("SELECT * FROM workspace_memories WHERE id=?1 AND workspace_path=?2", id, workspace);
                if (rows.Count == 0) throw new InvalidOperationException("找不到这条工作区记忆");
                if (active) ValidateMemoryCapacity(db, workspace, id, Convert.ToString(rows[0]["content"]).Length);
                db.Execute("UPDATE workspace_memories SET state=?1,updated_at=?2 WHERE id=?3 AND workspace_path=?4",
                    active ? "active" : "inactive", ProviderStore.NowIso(), id, workspace);
                return MemoryItem(db.Query("SELECT * FROM workspace_memories WHERE id=?1", id).First());
            }
        }

        public bool DeleteWorkspaceMemory(string workspace, string id)
        {
            workspace = NormalizeWorkspace(workspace);
            Guid parsed;
            if (!Guid.TryParse((id ?? "").Trim(), out parsed)) throw new InvalidOperationException("记忆 ID 无效");
            lock (_gate) using (var db = SqliteDb.Open(_path))
            {
                db.Execute("DELETE FROM workspace_memories WHERE id=?1 AND workspace_path=?2", id, workspace);
                return db.ScalarInt64("SELECT changes()") == 1;
            }
        }

        private static void ValidateMemoryCapacity(SqliteDb db, string workspace, string excludingId, int incomingCharacters)
        {
            var active = db.ScalarInt64("SELECT COUNT(*) FROM workspace_memories WHERE workspace_path=?1 AND state='active' AND id<>?2", workspace, excludingId ?? "");
            if (active >= 32) throw new InvalidOperationException("当前工作区最多启用 32 条长期记忆；请先停用一条");
            var characters = db.ScalarInt64("SELECT COALESCE(SUM(LENGTH(content)),0) FROM workspace_memories WHERE workspace_path=?1 AND state='active' AND id<>?2", workspace, excludingId ?? "");
            if (characters + incomingCharacters > 24000) throw new InvalidOperationException("当前工作区启用记忆正文合计不能超过 24,000 个字符；请缩短或停用部分记忆");
        }

        private static string NormalizeWorkspace(string workspace)
        {
            var full = System.IO.Path.GetFullPath((workspace ?? "").Trim());
            var root = System.IO.Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? root : full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }

        private static JObject MemoryItem(Dictionary<string, object> row)
        {
            var content = Convert.ToString(row["content"]);
            return new JObject
            {
                ["id"] = Convert.ToString(row["id"]), ["workspace"] = Convert.ToString(row["workspace_path"]),
                ["title"] = Convert.ToString(row["title"]), ["content"] = content,
                ["active"] = string.Equals(Convert.ToString(row["state"]), "active", StringComparison.Ordinal),
                ["source"] = Convert.ToString(row["source"]), ["sourceRunId"] = Convert.ToString(row["source_run_id"]),
                ["estimatedTokens"] = ContextBudgetPlanner.EstimateTokens(content),
                ["createdAt"] = Convert.ToString(row["created_at"]), ["updatedAt"] = Convert.ToString(row["updated_at"])
            };
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
                ["artifacts"] = db.ScalarInt64("SELECT COUNT(*) FROM artifacts"), ["contextEntries"] = db.ScalarInt64("SELECT COUNT(*) FROM context_entries"),
                ["providerHealthEntries"] = db.ScalarInt64("SELECT COUNT(*) FROM provider_health"),
                ["workspaceMemories"] = db.ScalarInt64("SELECT COUNT(*) FROM workspace_memories")
                , ["integrity"] = db.ScalarString("PRAGMA integrity_check") ?? "unknown"
            };
        }

        private static void NormalizeToolCalls(SqliteDb db, string runId, string taskId, string payload, string now, JObject policyManifest)
        {
            JObject value; try { value = JObject.Parse(payload); } catch { return; }
            var policy = ToolRuntimeSettings.From(policyManifest);
            var type = (string)value["type"] ?? ""; var blocks = value["message"]?["content"] as JArray ?? new JArray();
            if (type == "assistant")
            {
                foreach (var block in blocks.OfType<JObject>().Where(block => (string)block["type"] == "tool_use"))
                {
                    var id = (string)block["id"] ?? ""; if (id.Length == 0) continue;
                    var input = ToolRuntimePolicy.Prepare(block["input"] ?? new JObject(), policy.MaxInputBytes, true);
                    db.Execute("INSERT OR IGNORE INTO tool_calls(id,run_id,task_id,tool_name,state,input_json,input_meta_json,started_at,updated_at) VALUES(?1,?2,?3,?4,'running',?5,?6,?7,?7)",
                        id, runId, taskId, (string)block["name"] ?? "unknown", input.Json, input.Metadata.ToString(Formatting.None), now);
                }
            }
            else if (type == "user")
            {
                foreach (var block in blocks.OfType<JObject>().Where(block => (string)block["type"] == "tool_result"))
                {
                    var id = (string)block["tool_use_id"] ?? ""; if (id.Length == 0) continue;
                    var output = ToolRuntimePolicy.Prepare(block["content"] ?? JValue.CreateString(""), policy.MaxOutputBytes, false);
                    var failed = (bool?)block["is_error"] ?? false;
                    bool ignored; var error = failed ? ToolRuntimePolicy.RedactAndLimit(output.Json, policy.MaxErrorBytes, out ignored) : "";
                    db.Execute(@"UPDATE tool_calls SET state=?2,output_json=?3,output_meta_json=?4,error_text=?5,terminal_reason=?6,
duration_ms=MAX(0,CAST((julianday(?7)-julianday(started_at))*86400000 AS INTEGER)),updated_at=?7 WHERE id=?1",
                        id, failed ? "failed" : "completed", output.Json, output.Metadata.ToString(Formatting.None), error, failed ? "tool_result_error" : "tool_result", now);
                }
            }
        }

        private static JToken ParseJson(string value) { if (string.IsNullOrWhiteSpace(value)) return null; try { return JToken.Parse(value); } catch { return JValue.CreateString(value); } }
        private static string LimitText(string value, int max) { return string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value.Substring(0, max); }
        private static long EstimateTokens(string value) { return ContextBudgetPlanner.EstimateTokens(value); }
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
                if (store.TaskState("task-live") != JobStates.Running) return 64;
                store.UpsertRun("run-live-other", "task-live", "request-live-other", "offline", AppPaths.Workspace, JobStates.Running, 124);
                store.UpdateRunState("run-live", JobStates.Completed, 123, "{}");
                if (store.TaskState("task-live") != JobStates.Running) return 65;
                store.UpdateRunState("run-live-other", JobStates.Completed, 124, "{}");
                if (store.TaskState("task-live") != JobStates.Completed) return 66;
                store.UpsertRun("run-live", "task-live", "request-live", "offline", AppPaths.Workspace, JobStates.Running, 123);
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"result\",\"text\":\"SQLite 中文\"}");
                long next; var events = store.ReadEvents("run-live", 0, 10, out next);
                if (next != 1 || events.Count != 1 || !((string)events[0]["payload"]).Contains("SQLite 中文")) return 34;
                if (store.FindRunByRequestId("request-live") != "run-live") return 35;
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-one\",\"name\":\"Read\",\"input\":{\"file_path\":\"demo.txt\"}}]}}", "test:tool:start");
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-one\",\"content\":\"ok\"}]}}", "test:tool:result");
                var tools = store.ReadToolCalls("run-live");
                if (tools.Count != 1 || (string)tools[0]["state"] != "completed" || (string)tools[0]["toolName"] != "Read") return 36;
                var oversizedInput = new JObject
                {
                    ["authorization"] = "Bearer secret-value-that-must-not-persist",
                    ["api_key"] = "sk-tool-runtime-secret-value",
                    ["payload"] = new string('参', 30000)
                };
                store.AppendWorkerEvent("run-live", "task-live", new JObject
                {
                    ["type"] = "assistant", ["message"] = new JObject { ["content"] = new JArray(new JObject
                    {
                        ["type"] = "tool_use", ["id"] = "tool-large", ["name"] = "Bash", ["input"] = oversizedInput
                    }) }
                }.ToString(Formatting.None), "test:tool:large:start");
                store.AppendWorkerEvent("run-live", "task-live", new JObject
                {
                    ["type"] = "user", ["message"] = new JObject { ["content"] = new JArray(new JObject
                    {
                        ["type"] = "tool_result", ["tool_use_id"] = "tool-large",
                        ["content"] = new string('结', 220000) + " sk-tool-output-secret-value"
                    }) }
                }.ToString(Formatting.None), "test:tool:large:result");
                var large = store.ReadToolCalls("run-live").OfType<JObject>().First(item => (string)item["id"] == "tool-large");
                if ((bool?)large["inputPersistence"]?["truncated"] != true || (bool?)large["outputPersistence"]?["truncated"] != true) return 50;
                if ((bool?)large["inputPersistence"]?["redacted"] != true || (bool?)large["outputPersistence"]?["redacted"] != true) return 51;
                var persistedTool = large.ToString(Formatting.None);
                if (persistedTool.Contains("secret-value-that-must-not-persist") || persistedTool.Contains("sk-tool-runtime-secret-value") || persistedTool.Contains("sk-tool-output-secret-value")) return 52;
                long toolNext; var persistedEvents = store.ReadEvents("run-live", 0, 50, out toolNext).ToString(Formatting.None);
                if (persistedEvents.Contains("secret-value-that-must-not-persist") || persistedEvents.Contains("sk-tool-runtime-secret-value") || persistedEvents.Contains("sk-tool-output-secret-value")) return 53;
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-cancel\",\"name\":\"Bash\",\"input\":{}}]}}", "test:tool:cancel:start");
                if (store.TransitionActiveToolCalls("run-live", "cancelled", "user_cancelled") != 1) return 54;
                store.AppendWorkerEvent("run-live", "task-live", "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tool-timeout\",\"name\":\"Bash\",\"input\":{}}]}}", "test:tool:timeout:start");
                System.Threading.Thread.Sleep(20);
                if (store.MarkTimedOutToolCalls("run-live", 0).Count != 1) return 55;
                var terminalTools = store.ReadToolCalls("run-live").OfType<JObject>().ToDictionary(item => (string)item["id"]);
                if ((string)terminalTools["tool-cancel"]["state"] != "cancelled" || (string)terminalTools["tool-timeout"]["state"] != "timed_out") return 56;
                store.RecordContext("run-live", "task-live", "user", "测试上下文", "中文上下文", new JObject());
                if ((long)store.ContextReport("run-live")["estimatedTokens"] <= 0) return 37;
                var artifactPath = System.IO.Path.Combine(AppPaths.Workspace, "artifact-test.txt"); File.WriteAllText(artifactPath, "artifact", new UTF8Encoding(false));
                store.RecordArtifact("task-live", "run-live", "test", artifactPath, "text/plain", new JObject());
                if (store.ListArtifacts("run-live").Count != 1) return 38;
                var memoryOneId = Guid.NewGuid().ToString();
                var memoryTwoId = Guid.NewGuid().ToString();
                var memoryOne = store.SaveWorkspaceMemory(AppPaths.Workspace, memoryOneId, "默认语言", "默认使用中文回答，专有名词保留英文。", true);
                store.SaveWorkspaceMemory(AppPaths.Workspace, memoryTwoId, "暂不注入", "这条记忆处于停用状态。", false);
                if ((bool?)memoryOne["active"] != true || (long?)memoryOne["estimatedTokens"] <= 0L) return 67;
                if (store.ListWorkspaceMemories(AppPaths.Workspace, false).Count != 1 || store.ListWorkspaceMemories(AppPaths.Workspace, true).Count != 2) return 68;
                if ((bool?)store.SetWorkspaceMemoryActive(AppPaths.Workspace, memoryTwoId, true)["active"] != true || store.ListWorkspaceMemories(AppPaths.Workspace, false).Count != 2) return 69;
                var updatedMemory = store.SaveWorkspaceMemory(AppPaths.Workspace, memoryOneId, "默认语言（更新）", "默认使用中文；技术名词保留英文。", true);
                if ((string)updatedMemory["title"] != "默认语言（更新）") return 70;
                if (!store.DeleteWorkspaceMemory(AppPaths.Workspace, memoryTwoId) || store.ListWorkspaceMemories(AppPaths.Workspace, true).Count != 1) return 71;
                var reopenedMemories = new AgentEventStore(); reopenedMemories.InitializeAndMigrate();
                if (reopenedMemories.ListWorkspaceMemories(AppPaths.Workspace, false).Count != 1) return 72;
                if (!Directory.GetDirectories(AppPaths.Data, "migration-backup-*").Any()) return 39;
                var scheduleId = "schedule-durable"; var due = DateTimeOffset.UtcNow.AddSeconds(-1);
                store.ReplaceSchedules(new JArray(new JObject { ["id"] = scheduleId, ["sessionId"] = "task-live", ["enabled"] = true, ["at"] = due.ToString("o"), ["text"] = "可靠调度", ["maxRetries"] = 1 }));
                if (store.ClaimDueSchedules("host:test", DateTimeOffset.UtcNow, 5).Count != 1) return 40;
                if (store.ClaimDueSchedules("host:other", DateTimeOffset.UtcNow, 5).Count != 0) return 41;
                var trackedSchedule = "schedule-tracked";
                store.ReplaceSchedules(new JArray(new JObject { ["id"] = trackedSchedule, ["sessionId"] = "task-live", ["enabled"] = true, ["at"] = due.ToString("o"), ["text"] = "可追踪调度", ["maxRetries"] = 0 }));
                if (store.ClaimDueSchedules("host:test", DateTimeOffset.UtcNow, 5).Count != 1) return 61;
                store.UpsertRun("run-scheduled", "task-live", "request-scheduled", "offline", AppPaths.Workspace, JobStates.Running, 123);
                var boundSchedule = store.BindScheduleRun(trackedSchedule, "run-scheduled", "host:test", DateTimeOffset.UtcNow);
                if (boundSchedule == null || (string)boundSchedule["activeRunId"] != "run-scheduled" || (string)boundSchedule["lastRunId"] != "run-scheduled") return 62;
                store.UpdateRunState("run-scheduled", JobStates.Completed, 123, new JObject { ["result"] = "schedule done" }.ToString(Formatting.None));
                var scheduleResult = store.CompleteSchedule(trackedSchedule, true, "", DateTimeOffset.UtcNow);
                if (scheduleResult == null || (string)scheduleResult["state"] != "completed" || (string)scheduleResult["lastRunId"] != "run-scheduled" || !string.IsNullOrWhiteSpace((string)scheduleResult["activeRunId"])) return 63;
                store.CompleteSchedule(scheduleId, false, "first failure sk-secret-value", DateTimeOffset.UtcNow);
                store.DeferSchedule(scheduleId, DateTimeOffset.UtcNow.AddSeconds(-1), "retry-now");
                if (store.ClaimDueSchedules("host:test", DateTimeOffset.UtcNow, 5).Count != 1) return 42;
                store.CompleteSchedule(scheduleId, false, "second failure", DateTimeOffset.UtcNow);
                var deadSchedule = store.ListSchedules(true).OfType<JObject>().FirstOrDefault(item => (string)item["id"] == scheduleId);
                if (deadSchedule == null || (string)deadSchedule["state"] != "dead_letter") return 43;
                if (!store.ReplayDeadLetter(scheduleId) || (string)store.ListSchedules(true).OfType<JObject>().First(item => (string)item["id"] == scheduleId)["state"] != "scheduled") return 44;
                store.RecordFork("task-live", "task-fork", "checkpoint-one", new JObject { ["permissionInheritance"] = "restrict-only" });
                if (store.ListForks("task-live").Count != 1 || (string)store.ListForks("task-fork")[0]["parentTaskId"] != "task-live") return 45;
                store.UpsertRun("run-child", "task-child", "request-child", "offline", AppPaths.Workspace, JobStates.Starting, 0);
                store.CreateChildAgent("run-live", "run-child", "task-child", 1, new JObject { ["name"] = "测试子 Agent" });
                store.UpdateRunState("run-child", JobStates.Running, 456, "{}");
                if ((string)store.ListChildAgents("run-live")[0]["state"] != JobStates.Running) return 57;
                store.UpdateRunState("run-child", JobStates.Completed, 456, new JObject { ["result"] = "子 Agent 中文成果" }.ToString(Formatting.None));
                var child = store.ListChildAgents("run-live")[0] as JObject;
                if (child == null || (string)child["state"] != JobStates.Completed || (string)child["handoffState"] != "ready" || !child["result"].ToString().Contains("子 Agent 中文成果")) return 58;
                var handoff = store.AcceptChildHandoff("run-child", "run-live");
                if (handoff == null || (string)handoff["handoffState"] != "accepted") return 59;
                store.UpsertRun("run-failed-child", "task-failed-child", "request-failed-child", "offline", AppPaths.Workspace, JobStates.Failed, 0);
                Directory.CreateDirectory(System.IO.Path.Combine(AppPaths.Runs, "run-failed-child"));
                JsonUtil.WriteAtomic(System.IO.Path.Combine(AppPaths.Runs, "run-failed-child", "request.json"), new JObject { ["prompt"] = "retry fixture", ["childName"] = "retry" });
                store.CreateChildAgent("run-live", "run-failed-child", "task-failed-child", 1, new JObject { ["name"] = "retry", ["attempt"] = 1, ["maxRetries"] = 2 });
                store.UpdateRunState("run-failed-child", JobStates.Failed, 0, new JObject { ["error"] = "fixture failure" }.ToString(Formatting.None));
                var retryRequest = store.PrepareChildRetry("run-failed-child", "run-live");
                if (retryRequest == null || (int?)retryRequest["attempt"] != 2 || (string)retryRequest["retryOf"] != "run-failed-child") return 60;
                var cooling = store.RecordProviderProbe("provider-health-test", false, "rate_limit", "429 sk-provider-secret-value", 120, 1, "selftest");
                if ((string)cooling["state"] != "cooling" || (bool?)cooling["available"] != false || ((string)cooling["lastError"] ?? "").Contains("sk-provider-secret-value")) return 46;
                System.Threading.Thread.Sleep(1100);
                var recoveredHealth = store.ListProviderHealth("provider-health-test").First as JObject;
                if (recoveredHealth == null || (bool?)recoveredHealth["available"] != true || (string)recoveredHealth["state"] != "probe_failed") return 47;
                var healthy = store.RecordProviderOutcome("provider-health-test", "healthy-model", true, "", "", 85, 0, "selftest");
                var unavailable = store.RecordProviderOutcome("provider-health-test", "missing-model", false, "model_unavailable", "模型不存在", 91, 300, "selftest");
                if ((string)unavailable["state"] != "unavailable" || (bool?)unavailable["available"] != false ||
                    (long?)unavailable["cooldownRemainingSeconds"] != 0 || (bool?)store.GetProviderHealth("provider-health-test", "missing-model")?["available"] != false) return 61;
                if ((string)healthy["state"] != "healthy" || (long?)healthy["successCount"] != 1L) return 48;
                store.RecordProviderProbe("stale-provider-health", false, "unknown", "stale fixture", 1, 0, "selftest");
                if (store.PruneProviderHealth(new[] { "provider-health-test" }) != 1 || store.ListProviderHealth("stale-provider-health").Count != 0 || store.ListProviderHealth("provider-health-test").Count < 2) return 64;
                var finalHealth = store.Health();
                if ((string)finalHealth["schemaVersion"] != "11" || (long?)finalHealth["providerHealthEntries"] < 2L || (long?)finalHealth["workspaceMemories"] != 1L) return 49;
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    /// <summary>
    /// Project, transcript, Git and extension APIs.  Claude's JSONL transcript is
    /// treated as the durable conversation source; GUI JSON is only a compatibility
    /// cache for conversations created by older Workbench builds.
    /// </summary>
    internal sealed class WorkbenchApi
    {
        private readonly object _gitGate = new object();
        private readonly object _terminalGate = new object();
        private readonly AgentEventStore _eventStore;
        private readonly Dictionary<string, ConPtyTerminalSession> _terminals = new Dictionary<string, ConPtyTerminalSession>(StringComparer.OrdinalIgnoreCase);
        private NativeHost _window;

        private string RegistryFile { get { return Path.Combine(AppPaths.Data, "projects.json"); } }
        private string IgnoredRegistryFile { get { return Path.Combine(AppPaths.Data, "projects-hidden.json"); } }
        private string SkinRoot { get { return Path.Combine(AppPaths.Data, "skins"); } }
        private string UpdateChannelFile { get { return Path.Combine(AppPaths.Data, "update-channel.json"); } }
        private string ClaudeProjectsRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"); }
        }

        public WorkbenchApi(AgentEventStore eventStore) { _eventStore = eventStore ?? throw new ArgumentNullException("eventStore"); }

        public void AttachWindow(NativeHost window) { _window = window; }

        public async Task<bool> HandleAsync(HttpListenerContext context, string path, string method)
        {
            if (!path.StartsWith("/api/workbench/", StringComparison.Ordinal)) return false;

            if (method == "GET" && path == "/api/workbench/projects")
            {
                await WriteJson(context.Response, ListProjects()); return true;
            }
            if (method == "POST" && path == "/api/workbench/projects/add")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                var selected = ((string)body["path"] ?? "").Trim();
                if (selected.Length == 0 && _window != null) selected = await _window.SelectFolderAsync();
                if (selected.Length == 0) { await WriteJson(context.Response, new JObject { ["cancelled"] = true }); return true; }
                selected = Path.GetFullPath(selected);
                if (!Directory.Exists(selected)) { await Error(context.Response, "目录不存在", 404); return true; }
                SaveProject(selected);
                await WriteJson(context.Response, ProjectObject(selected)); return true;
            }
            if (method == "POST" && path == "/api/workbench/projects/remove")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                RemoveProject((string)body["path"]);
                await WriteJson(context.Response, new JObject { ["ok"] = true, ["filesDeleted"] = false }); return true;
            }
            if (method == "GET" && path == "/api/workbench/transcripts")
            {
                var query = context.Request.QueryString["query"] ?? "";
                var workspace = context.Request.QueryString["workspace"] ?? "";
                await WriteJson(context.Response, ListTranscripts(workspace, query)); return true;
            }
            if (method == "GET" && path.StartsWith("/api/workbench/transcripts/", StringComparison.Ordinal))
            {
                var id = SafeId(path.Substring("/api/workbench/transcripts/".Length));
                var file = FindTranscript(id, context.Request.QueryString["workspace"] ?? "");
                if (file == null) { await Error(context.Response, "找不到 Claude transcript", 404); return true; }
                await WriteJson(context.Response, ParseTranscript(file, true)); return true;
            }
            if (method == "GET" && path == "/api/workbench/tree")
            {
                await WriteJson(context.Response, ListTree(context.Request.QueryString["workspace"], context.Request.QueryString["path"])); return true;
            }
            if (method == "GET" && path == "/api/workbench/files/search")
            {
                await WriteJson(context.Response, SearchFiles(context.Request.QueryString["workspace"], context.Request.QueryString["query"])); return true;
            }
            if (path == "/api/workbench/file" && method == "GET")
            {
                await WriteJson(context.Response, ReadFile(context.Request.QueryString["workspace"], context.Request.QueryString["path"])); return true;
            }
            if (path == "/api/workbench/file" && method == "POST")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, SaveFile((string)body["workspace"], (string)body["path"], (string)body["content"])); return true;
            }
            if (method == "GET" && path == "/api/workbench/git/status")
            {
                await WriteJson(context.Response, GitStatus(context.Request.QueryString["workspace"])); return true;
            }
            if (method == "GET" && path == "/api/workbench/git/diff")
            {
                await WriteJson(context.Response, GitDiff(context.Request.QueryString["workspace"], context.Request.QueryString["staged"] == "1")); return true;
            }
            if (method == "POST" && path == "/api/workbench/git/action")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, GitAction(body)); return true;
            }
            if (method == "GET" && path == "/api/workbench/extensions")
            {
                await WriteJson(context.Response, Extensions(context.Request.QueryString["workspace"])); return true;
            }
            if (method == "GET" && path == "/api/workbench/skins")
            {
                await WriteJson(context.Response, ListSkins()); return true;
            }
            if (method == "POST" && path == "/api/workbench/skins/import")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, await ImportSkin(body)); return true;
            }
            if (method == "POST" && path == "/api/workbench/skins/delete")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, DeleteSkin((string)body["id"])); return true;
            }
            if (method == "GET" && path.StartsWith("/api/workbench/skins/asset/", StringComparison.Ordinal))
            {
                await ServeSkinAsset(context.Response, path.Substring("/api/workbench/skins/asset/".Length)); return true;
            }
            if (method == "POST" && path == "/api/workbench/extensions/scaffold")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, ScaffoldExtension(body)); return true;
            }
            if (method == "POST" && path == "/api/workbench/extensions/import")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, await ImportExtension(body)); return true;
            }
            if (method == "POST" && path == "/api/workbench/extensions/delete")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, DeleteExtension(body)); return true;
            }
            if (method == "GET" && path == "/api/workbench/usage")
            {
                await WriteJson(context.Response, Usage(context.Request.QueryString["workspace"])); return true;
            }
            if (method == "GET" && path == "/api/workbench/health")
            {
                await WriteJson(context.Response, Health(context.Request.QueryString["workspace"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/reliability/selftest")
            {
                await WriteJson(context.Response, ReliabilitySelfTest.Run()); return true;
            }
            if (method == "POST" && path == "/api/workbench/diagnostics/export")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, NativeDiagnostics.Export(_eventStore, (string)body["workspace"] ?? AppPaths.Workspace)); return true;
            }
            if (method == "GET" && path == "/api/workbench/update")
            {
                await WriteJson(context.Response, UpdateStatus()); return true;
            }
            if (method == "POST" && path == "/api/workbench/update/config")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, SaveUpdateConfig(body)); return true;
            }
            if (method == "POST" && path == "/api/workbench/update/check")
            {
                await WriteJson(context.Response, await CheckUpdate()); return true;
            }
            if (method == "POST" && path == "/api/workbench/update/stage")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, await StageUpdate(body)); return true;
            }
            if (method == "POST" && path == "/api/workbench/terminal")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                var root = NormalizeExistingDirectory((string)body["workspace"], true);
                var command = ((string)body["command"] ?? "").Trim();
                if (command.Length == 0) { await Error(context.Response, "命令不能为空", 400); return true; }
                var result = Run("cmd.exe", "/d /s /c " + Quote(command), root, 120000);
                await WriteJson(context.Response, new JObject { ["exitCode"] = result.ExitCode, ["output"] = Limit(result.Output, 500000), ["error"] = Limit(result.Error, 100000) }); return true;
            }
            if (method == "POST" && path == "/api/workbench/terminal/start")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, StartTerminal((string)body["workspace"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/terminal/write")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, WriteTerminal((string)body["id"], (string)body["command"], (bool?)body["submit"] ?? true)); return true;
            }
            if (method == "POST" && path == "/api/workbench/terminal/resize")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, ResizeTerminal((string)body["id"], (int?)body["columns"] ?? 120, (int?)body["rows"] ?? 36)); return true;
            }
            if (method == "GET" && path == "/api/workbench/terminal/poll")
            {
                await WriteJson(context.Response, PollTerminal(context.Request.QueryString["id"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/terminal/stop")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, StopTerminal((string)body["id"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/clipboard")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, SaveClipboardImage(body)); return true;
            }
            if (method == "POST" && path == "/api/workbench/notify")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                if (_window != null) _window.ShowNotification((string)body["title"], (string)body["message"]);
                await WriteJson(context.Response, new JObject { ["ok"] = true }); return true;
            }
            if (path == "/api/workbench/schedules" && method == "GET")
            {
                await WriteJson(context.Response, _eventStore.ListSchedules(true)); return true;
            }
            if (path == "/api/workbench/schedules" && method == "POST")
            {
                var schedules = await ApiServer.ReadBody(context.Request) as JArray;
                if (schedules == null || schedules.Count > 500) { await Error(context.Response, "调度数据无效或数量超过 500", 400); return true; }
                var count = _eventStore.ReplaceSchedules(schedules);
                await WriteJson(context.Response, new JObject { ["ok"] = true, ["count"] = count, ["store"] = "sqlite" }); return true;
            }
            if (path == "/api/workbench/schedules/replay" && method == "POST")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, new JObject { ["ok"] = _eventStore.ReplayDeadLetter((string)body["id"] ?? "") }); return true;
            }
            if (method == "GET" && path == "/api/workbench/checkpoints")
            {
                await WriteJson(context.Response, ListCheckpoints(context.Request.QueryString["sessionId"])); return true;
            }
            if (method == "GET" && path == "/api/workbench/task/forks")
            {
                await WriteJson(context.Response, _eventStore.ListForks(context.Request.QueryString["sessionId"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/checkpoints")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, CreateCheckpoint((string)body["sessionId"], (string)body["workspace"], (string)body["label"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/checkpoints/restore")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, RestoreCheckpoint((string)body["sessionId"], (string)body["workspace"], (string)body["checkpointId"], (bool?)body["fork"] ?? false)); return true;
            }
            if (method == "GET" && path == "/api/workbench/task/isolation")
            {
                await WriteJson(context.Response, TaskWorkspaceManager.Describe(context.Request.QueryString["jobId"])); return true;
            }
            if (method == "POST" && path == "/api/workbench/task/apply")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, TaskWorkspaceManager.Apply((string)body["jobId"], body["paths"] as JArray ?? new JArray())); return true;
            }
            if (method == "POST" && path == "/api/workbench/task/revert")
            {
                var body = JsonUtil.ObjectOrEmpty(await ApiServer.ReadBody(context.Request));
                await WriteJson(context.Response, TaskWorkspaceManager.Revert((string)body["jobId"])); return true;
            }
            return false;
        }

        private JArray ListProjects()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AppPaths.Workspace };
            foreach (var token in JsonUtil.Read(RegistryFile, new JArray()) as JArray ?? new JArray())
            {
                var value = ((string)token ?? "").Trim();
                if (Directory.Exists(value)) paths.Add(Path.GetFullPath(value));
            }
            foreach (var token in JsonUtil.Read(AppPaths.SessionsFile, new JArray()) as JArray ?? new JArray())
            {
                var value = ((string)token["workspace"] ?? "").Trim();
                if (Directory.Exists(value)) paths.Add(Path.GetFullPath(value));
            }
            foreach (var file in TranscriptFiles())
            {
                var cwd = TranscriptCwd(file);
                if (Directory.Exists(cwd)) paths.Add(Path.GetFullPath(cwd));
            }
            var hidden = new HashSet<string>((JsonUtil.Read(IgnoredRegistryFile, new JArray()) as JArray ?? new JArray()).Select(value => (string)value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            return new JArray(paths.Where(value => !hidden.Contains(Path.GetFullPath(value))).OrderBy(value => value).Select(ProjectObject));
        }

        private JObject ProjectObject(string path)
        {
            var full = Path.GetFullPath(path);
            var transcripts = TranscriptFiles().Count(file => string.Equals(TranscriptCwd(file), full, StringComparison.OrdinalIgnoreCase));
            return new JObject
            {
                ["path"] = full,
                ["name"] = new DirectoryInfo(full).Name,
                ["transcriptCount"] = transcripts,
                ["git"] = Directory.Exists(Path.Combine(full, ".git"))
            };
        }

        private void SaveProject(string path)
        {
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in JsonUtil.Read(RegistryFile, new JArray()) as JArray ?? new JArray())
                if (!string.IsNullOrWhiteSpace((string)token)) values.Add(Path.GetFullPath((string)token));
            values.Add(Path.GetFullPath(path));
            JsonUtil.WriteAtomic(RegistryFile, new JArray(values.OrderBy(value => value)));
            var hidden = new HashSet<string>((JsonUtil.Read(IgnoredRegistryFile, new JArray()) as JArray ?? new JArray()).Select(value => (string)value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            hidden.Remove(Path.GetFullPath(path));
            JsonUtil.WriteAtomic(IgnoredRegistryFile, new JArray(hidden.OrderBy(value => value)));
        }

        private void RemoveProject(string path)
        {
            var target = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in JsonUtil.Read(RegistryFile, new JArray()) as JArray ?? new JArray())
            {
                var value = ((string)token ?? "").Trim();
                if (value.Length > 0 && !string.Equals(Path.GetFullPath(value), target, StringComparison.OrdinalIgnoreCase)) values.Add(Path.GetFullPath(value));
            }
            JsonUtil.WriteAtomic(RegistryFile, new JArray(values.OrderBy(value => value)));
            var hidden = new HashSet<string>((JsonUtil.Read(IgnoredRegistryFile, new JArray()) as JArray ?? new JArray()).Select(value => (string)value).Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            if (target.Length > 0) hidden.Add(target);
            JsonUtil.WriteAtomic(IgnoredRegistryFile, new JArray(hidden.OrderBy(value => value)));
        }

        private JArray ListTranscripts(string workspace, string query)
        {
            var normalizedWorkspace = NormalizeExistingDirectory(workspace, false);
            var needle = (query ?? "").Trim();
            var result = new List<JObject>();
            foreach (var file in TranscriptFiles())
            {
                var summary = ParseTranscript(file, needle.Length > 0);
                var cwd = (string)summary["workspace"] ?? "";
                if (normalizedWorkspace.Length > 0 && !string.Equals(cwd, normalizedWorkspace, StringComparison.OrdinalIgnoreCase)) continue;
                if (needle.Length > 0)
                {
                    var haystack = ((string)summary["searchText"] ?? "") + "\n" + ((string)summary["title"] ?? "");
                    if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                summary.Remove("messages");
                summary.Remove("searchText");
                result.Add(summary);
            }
            return new JArray(result.OrderByDescending(value => (string)value["updatedAt"]).Take(500));
        }

        private IEnumerable<string> TranscriptFiles()
        {
            if (!Directory.Exists(ClaudeProjectsRoot)) return Enumerable.Empty<string>();
            try { return Directory.EnumerateFiles(ClaudeProjectsRoot, "*.jsonl", SearchOption.AllDirectories).ToArray(); }
            catch { return Enumerable.Empty<string>(); }
        }

        private string FindTranscript(string id, string workspace)
        {
            var expectedWorkspace = NormalizeExistingDirectory(workspace, false);
            return TranscriptFiles().FirstOrDefault(file =>
                string.Equals(Path.GetFileNameWithoutExtension(file), id, StringComparison.OrdinalIgnoreCase) &&
                (expectedWorkspace.Length == 0 || string.Equals(TranscriptCwd(file), expectedWorkspace, StringComparison.OrdinalIgnoreCase)));
        }

        private static string TranscriptCwd(string file)
        {
            try
            {
                foreach (var line in ReadLinesShared(file).Take(80))
                {
                    JObject value;
                    try { value = JObject.Parse(line); } catch { continue; }
                    var cwd = ((string)value["cwd"] ?? "").Trim();
                    if (cwd.Length > 0) return Path.GetFullPath(cwd);
                }
            }
            catch { }
            return "";
        }

        private static JObject ParseTranscript(string file, bool includeMessages)
        {
            var messages = new JArray();
            var assistantByMessageId = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var title = "";
            var cwd = "";
            var branch = "";
            var created = File.GetCreationTimeUtc(file).ToString("o");
            var updated = File.GetLastWriteTimeUtc(file).ToString("o");
            var search = new StringBuilder();
            long input = 0, output = 0, cacheRead = 0, cacheCreate = 0;
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var usageByModel = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in ReadLinesShared(file))
            {
                JObject entry;
                try { entry = JObject.Parse(line); } catch { continue; }
                if (cwd.Length == 0) cwd = ((string)entry["cwd"] ?? "").Trim();
                if (branch.Length == 0) branch = ((string)entry["gitBranch"] ?? "").Trim();
                if ((string)entry["type"] == "ai-title" && !string.IsNullOrWhiteSpace((string)entry["aiTitle"])) title = (string)entry["aiTitle"];
                var type = (string)entry["type"] ?? "";
                var source = entry["message"] as JObject;
                if (type == "user" && source != null)
                {
                    var text = ExtractText(source["content"]);
                    if (text.Length == 0) continue;
                    if (title.Length == 0) title = OneLine(text, 64);
                    search.AppendLine(text);
                    if (includeMessages) messages.Add(new JObject
                    {
                        ["id"] = (string)entry["uuid"] ?? Guid.NewGuid().ToString(), ["role"] = "user", ["text"] = text,
                        ["time"] = (string)entry["timestamp"] ?? created, ["transcript"] = true
                    });
                }
                else if (type == "assistant" && source != null)
                {
                    var messageId = (string)source["id"] ?? (string)entry["uuid"] ?? Guid.NewGuid().ToString();
                    var text = ExtractText(source["content"]);
                    if (text.Length > 0) search.AppendLine(text);
                    var model = ((string)source["model"] ?? "").Trim();
                    if (model.Length > 0) models.Add(model);
                    var usage = source["usage"] as JObject;
                    if (usage != null)
                    {
                        var messageInput = (long?)usage["input_tokens"] ?? 0;
                        var messageOutput = (long?)usage["output_tokens"] ?? 0;
                        var messageCacheRead = (long?)usage["cache_read_input_tokens"] ?? 0;
                        var messageCacheCreate = (long?)usage["cache_creation_input_tokens"] ?? 0;
                        input += messageInput; output += messageOutput; cacheRead += messageCacheRead; cacheCreate += messageCacheCreate;
                        if (model.Length > 0) Accumulate(usageByModel, model, messageInput + messageCacheRead + messageCacheCreate, messageOutput,
                            messageInput + messageOutput + messageCacheRead + messageCacheCreate);
                    }
                    if (!includeMessages) continue;
                    JObject message;
                    if (!assistantByMessageId.TryGetValue(messageId, out message))
                    {
                        message = new JObject
                        {
                            ["id"] = (string)entry["uuid"] ?? messageId, ["role"] = "assistant", ["text"] = "",
                            ["time"] = (string)entry["timestamp"] ?? updated, ["providerName"] = model, ["workflow"] = new JArray(), ["transcript"] = true
                        };
                        assistantByMessageId[messageId] = message;
                        messages.Add(message);
                    }
                    if (text.Length > 0) message["text"] = JoinText((string)message["text"], text);
                    var blocks = source["content"] as JArray ?? new JArray();
                    foreach (var block in blocks.OfType<JObject>())
                    {
                        var blockType = (string)block["type"] ?? "";
                        if (blockType == "tool_use")
                        {
                            ((JArray)message["workflow"]).Add(new JObject
                            {
                                ["id"] = (string)block["id"] ?? Guid.NewGuid().ToString(), ["title"] = (string)block["name"] ?? "工具调用",
                                ["detail"] = Limit(block["input"] == null ? "" : block["input"].ToString(Formatting.Indented), 12000), ["state"] = "done", ["open"] = false
                            });
                        }
                        else if (blockType == "thinking" && !string.IsNullOrWhiteSpace((string)block["thinking"]))
                        {
                            ((JArray)message["workflow"]).Add(new JObject
                            {
                                ["id"] = Guid.NewGuid().ToString(), ["title"] = "思考过程", ["detail"] = Limit((string)block["thinking"], 12000), ["state"] = "done", ["open"] = false
                            });
                        }
                    }
                    if (usage != null) message["usage"] = new JObject
                    {
                        ["input"] = (long?)usage["input_tokens"] ?? 0,
                        ["output"] = (long?)usage["output_tokens"] ?? 0,
                        ["total"] = ((long?)usage["input_tokens"] ?? 0) + ((long?)usage["output_tokens"] ?? 0) +
                            ((long?)usage["cache_read_input_tokens"] ?? 0) + ((long?)usage["cache_creation_input_tokens"] ?? 0),
                        ["cacheRead"] = (long?)usage["cache_read_input_tokens"] ?? 0,
                        ["cacheCreate"] = (long?)usage["cache_creation_input_tokens"] ?? 0
                    };
                }
            }
            return new JObject
            {
                ["id"] = Path.GetFileNameWithoutExtension(file), ["title"] = title.Length == 0 ? "未命名会话" : title,
                ["workspace"] = cwd, ["gitBranch"] = branch, ["createdAt"] = created, ["updatedAt"] = updated,
                ["messages"] = messages, ["messageCount"] = messages.Count, ["searchText"] = Limit(search.ToString(), 200000),
                ["models"] = new JArray(models), ["usageByModel"] = new JArray(usageByModel.Select(pair => pair.Value)), ["usage"] = new JObject
                {
                    ["input_tokens"] = input, ["output_tokens"] = output, ["cache_read_input_tokens"] = cacheRead,
                    ["cache_creation_input_tokens"] = cacheCreate, ["total_tokens"] = input + output + cacheRead + cacheCreate
                }
            };
        }

        private static JObject ListTree(string workspace, string relative)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var target = ResolveInside(root, relative ?? "");
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("目录不存在");
            var values = new List<JObject>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(target).Where(value => Path.GetFileName(value) != ".git").Take(500))
            {
                var info = File.GetAttributes(entry);
                var isDirectory = (info & FileAttributes.Directory) != 0;
                values.Add(new JObject
                {
                    ["name"] = Path.GetFileName(entry), ["path"] = Relative(root, entry), ["directory"] = isDirectory,
                    ["size"] = isDirectory ? 0 : new FileInfo(entry).Length, ["updatedAt"] = File.GetLastWriteTimeUtc(entry).ToString("o")
                });
            }
            return new JObject { ["root"] = root, ["path"] = Relative(root, target), ["entries"] = new JArray(values.OrderByDescending(v => (bool)v["directory"]).ThenBy(v => (string)v["name"])) };
        }

        private static JArray SearchFiles(string workspace, string query)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var needle = (query ?? "").Trim();
            if (needle.Length == 0) return new JArray();
            var ignored = new HashSet<string>(new[] { ".git", "node_modules", "bin", "obj", ".venv", "dist" }, StringComparer.OrdinalIgnoreCase);
            var results = new List<JObject>();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0 && results.Count < 50)
            {
                var directory = pending.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(directory); } catch { continue; }
                foreach (var entry in entries)
                {
                    var isDirectory = Directory.Exists(entry);
                    if (isDirectory)
                    {
                        if (!ignored.Contains(Path.GetFileName(entry))) pending.Push(entry);
                        continue;
                    }
                    var relative = Relative(root, entry);
                    if (relative.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    results.Add(new JObject { ["name"] = Path.GetFileName(entry), ["path"] = relative, ["fullPath"] = Path.GetFullPath(entry) });
                    if (results.Count >= 50) break;
                }
            }
            return new JArray(results.OrderBy(value => ((string)value["path"]).Length).ThenBy(value => (string)value["path"]));
        }

        private static JObject ReadFile(string workspace, string relative)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var file = ResolveInside(root, relative ?? "");
            if (!File.Exists(file)) throw new FileNotFoundException("文件不存在", file);
            var info = new FileInfo(file);
            if (info.Length > 3L * 1024 * 1024) throw new InvalidOperationException("文件超过 3 MB，请使用外部编辑器打开");
            var bytes = File.ReadAllBytes(file);
            if (bytes.Take(Math.Min(bytes.Length, 4096)).Any(value => value == 0)) throw new InvalidOperationException("二进制文件不能作为文本编辑");
            return new JObject { ["path"] = Relative(root, file), ["content"] = Encoding.UTF8.GetString(bytes), ["size"] = bytes.Length, ["updatedAt"] = info.LastWriteTimeUtc.ToString("o") };
        }

        private static JObject SaveFile(string workspace, string relative, string content)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var file = ResolveInside(root, relative ?? "");
            if (!File.Exists(file)) throw new FileNotFoundException("只允许保存已经存在的文件", file);
            var backup = file + ".claude-workbench.bak";
            File.Copy(file, backup, true);
            File.WriteAllText(file, content ?? "", new UTF8Encoding(false));
            return new JObject { ["saved"] = true, ["path"] = Relative(root, file), ["backup"] = backup };
        }

        private JObject GitStatus(string workspace)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var output = Run("git.exe", "status --porcelain=v1 -b", root, 12000);
            var lines = output.Output.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var files = new JArray(lines.Skip(1).Select(line => new JObject
            {
                ["index"] = line.Length > 0 ? line.Substring(0, 1) : " ", ["worktree"] = line.Length > 1 ? line.Substring(1, 1) : " ",
                ["path"] = line.Length > 3 ? line.Substring(3).Trim() : line
            }));
            return new JObject { ["branch"] = lines.FirstOrDefault() ?? "", ["files"] = files, ["clean"] = files.Count == 0, ["error"] = output.Error };
        }

        private JObject GitDiff(string workspace, bool staged)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var result = Run("git.exe", staged ? "diff --cached --no-ext-diff --no-color" : "diff --no-ext-diff --no-color", root, 20000);
            return new JObject { ["staged"] = staged, ["diff"] = Limit(result.Output, 1000000), ["error"] = result.Error, ["exitCode"] = result.ExitCode };
        }

        private JObject GitAction(JObject body)
        {
            var root = NormalizeExistingDirectory((string)body["workspace"], true);
            var action = ((string)body["action"] ?? "").Trim().ToLowerInvariant();
            var paths = (body["paths"] as JArray ?? new JArray()).Select(value => (string)value).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            string arguments;
            if (action == "stage") arguments = "add -- " + string.Join(" ", paths.Select(Quote));
            else if (action == "unstage") arguments = "restore --staged -- " + string.Join(" ", paths.Select(Quote));
            else if (action == "commit")
            {
                var message = ((string)body["message"] ?? "").Trim();
                if (message.Length == 0) throw new InvalidOperationException("提交说明不能为空");
                arguments = "commit -m " + Quote(message);
            }
            else if (action == "switch")
            {
                var branch = ((string)body["branch"] ?? "").Trim();
                if (branch.Length == 0 || branch.Any(ch => char.IsWhiteSpace(ch) || ch == ';' || ch == '&')) throw new InvalidOperationException("无效分支名");
                arguments = "switch " + Quote(branch);
            }
            else if (action == "push") arguments = "push";
            else throw new InvalidOperationException("不支持的 Git 操作");
            var result = Run("git.exe", arguments, root, 30000);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
            return new JObject { ["ok"] = true, ["output"] = result.Output, ["status"] = GitStatus(root) };
        }

        private static JObject Extensions(string workspace)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            var userClaude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            var projectClaude = Path.Combine(root, ".claude");
            var skills = new JArray();
            var agents = new JArray();
            foreach (var baseDir in new[] { Path.Combine(userClaude, "skills"), Path.Combine(projectClaude, "skills") })
                if (Directory.Exists(baseDir)) foreach (var dir in Directory.EnumerateDirectories(baseDir).Where(value => !Path.GetFileName(value).StartsWith(".", StringComparison.Ordinal)))
                {
                    var metadata = JsonUtil.Read(Path.Combine(dir, ".workbench-package.json"), new JObject()) as JObject ?? new JObject();
                    skills.Add(new JObject { ["name"] = Path.GetFileName(dir), ["path"] = dir, ["scope"] = baseDir.StartsWith(projectClaude, StringComparison.OrdinalIgnoreCase) ? "project" : "user", ["packageVersion"] = metadata["version"], ["signatureStatus"] = metadata["signatureStatus"] ?? "unmanaged" });
                }
            foreach (var baseDir in new[] { Path.Combine(userClaude, "agents"), Path.Combine(projectClaude, "agents") })
                if (Directory.Exists(baseDir)) foreach (var file in Directory.EnumerateFiles(baseDir, "*.md"))
                {
                    var name = Path.GetFileNameWithoutExtension(file); var metadata = JsonUtil.Read(Path.Combine(baseDir, "." + name + ".workbench-package.json"), new JObject()) as JObject ?? new JObject();
                    agents.Add(new JObject { ["name"] = name, ["path"] = file, ["scope"] = baseDir.StartsWith(projectClaude, StringComparison.OrdinalIgnoreCase) ? "project" : "user", ["packageVersion"] = metadata["version"], ["signatureStatus"] = metadata["signatureStatus"] ?? "unmanaged" });
                }
            var settings = MergeSettings(Path.Combine(userClaude, "settings.json"), Path.Combine(projectClaude, "settings.json"), Path.Combine(projectClaude, "settings.local.json"));
            var mcp = JsonUtil.Read(Path.Combine(root, ".mcp.json"), new JObject()) as JObject ?? new JObject();
            var hooks = settings["hooks"] as JObject ?? new JObject();
            var plugins = settings["enabledPlugins"] as JObject ?? new JObject();
            return new JObject
            {
                ["skills"] = skills, ["agents"] = agents,
                ["mcpServers"] = mcp["mcpServers"] ?? settings["mcpServers"] ?? new JObject(),
                ["hooks"] = hooks, ["plugins"] = plugins,
                ["claudeMd"] = new JArray(FindClaudeMd(root).Take(100)),
                ["userRoot"] = userClaude, ["projectRoot"] = projectClaude,
                ["mcpFile"] = Path.Combine(root, ".mcp.json"), ["mcpFileExists"] = File.Exists(Path.Combine(root, ".mcp.json"))
            };
        }

        private static JObject ScaffoldExtension(JObject body)
        {
            var root = NormalizeExistingDirectory((string)body["workspace"], true);
            var type = ((string)body["type"] ?? "").Trim().ToLowerInvariant();
            var name = ((string)body["name"] ?? "").Trim();
            if (name.Length < 2 || name.Length > 64 || name.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_'))
                throw new InvalidOperationException("名称只能包含字母、数字、连字符和下划线，长度为 2-64");
            var claudeRoot = Path.Combine(root, ".claude");
            string file;
            string content;
            if (type == "skill")
            {
                file = Path.Combine(claudeRoot, "skills", name, "SKILL.md");
                content = "---\nname: " + name + "\ndescription: 请补充这个 Skill 的适用场景和触发条件\n---\n\n# " + name + "\n\n请在这里编写执行步骤、约束和验收标准。\n";
            }
            else if (type == "agent")
            {
                file = Path.Combine(claudeRoot, "agents", name + ".md");
                content = "---\nname: " + name + "\ndescription: 请补充这个 Subagent 的职责\ntools: Read, Glob, Grep\n---\n\n你是一个专注于明确职责的 Subagent。请补充工作方法和输出要求。\n";
            }
            else throw new InvalidOperationException("仅支持创建 Skill 或 Agent");
            if (File.Exists(file)) throw new InvalidOperationException("同名扩展已经存在");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, content, new UTF8Encoding(false));
            return new JObject { ["ok"] = true, ["type"] = type, ["name"] = name, ["path"] = file };
        }

        private static JObject DeleteExtension(JObject body)
        {
            var root = NormalizeExistingDirectory((string)body["workspace"], true);
            var type = ((string)body["type"] ?? "").Trim().ToLowerInvariant();
            var name = ((string)body["name"] ?? "").Trim();
            if (name.Length < 1 || name.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')) throw new InvalidOperationException("扩展名称无效");
            var projectClaude = Path.Combine(root, ".claude");
            if (type == "skill")
            {
                var directory = ResolveInside(projectClaude, Path.Combine("skills", name));
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            else if (type == "agent")
            {
                var file = ResolveInside(projectClaude, Path.Combine("agents", name + ".md"));
                if (File.Exists(file)) File.Delete(file);
                var metadata = ResolveInside(projectClaude, Path.Combine("agents", "." + name + ".workbench-package.json"));
                if (File.Exists(metadata)) File.Delete(metadata);
            }
            else throw new InvalidOperationException("仅支持删除项目级 Skill 或 Agent");
            return new JObject { ["ok"] = true, ["type"] = type, ["name"] = name };
        }

        private async Task<JObject> ImportExtension(JObject body)
        {
            if (_window == null) throw new InvalidOperationException("当前窗口不可用");
            var workspace = NormalizeExistingDirectory((string)body["workspace"], true);
            var requestedPath = ((string)body["path"] ?? "").Trim();
            var packagePath = requestedPath.Length > 0 ? requestedPath : await _window.SelectExtensionPackageAsync();
            if (string.IsNullOrWhiteSpace(packagePath)) return new JObject { ["cancelled"] = true };
            packagePath = Path.GetFullPath(packagePath);
            if (!File.Exists(packagePath) || !string.Equals(Path.GetExtension(packagePath), ".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择 ZIP 格式的扩展包");
            if (new FileInfo(packagePath).Length > 48L * 1024 * 1024) throw new InvalidOperationException("扩展 ZIP 不能超过 48 MB");
            var incomingRoot = Path.Combine(AppPaths.Data, ".extension-incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(incomingRoot);
            try
            {
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    if (archive.Entries.Count == 0 || archive.Entries.Count > 300) throw new InvalidOperationException("扩展 ZIP 必须包含 1-300 个文件");
                    long expanded = 0;
                    foreach (var entry in archive.Entries)
                    {
                        var relative = SafeZipRelative(entry.FullName); expanded += entry.Length;
                        if (expanded > 96L * 1024 * 1024) throw new InvalidOperationException("扩展 ZIP 解压后不能超过 96 MB");
                        var destination = ResolveInside(incomingRoot, relative);
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(destination); continue; }
                        var extension = Path.GetExtension(destination).ToLowerInvariant();
                        var allowed = new[] { ".json", ".md", ".txt", ".yaml", ".yml", ".png", ".jpg", ".jpeg", ".webp", ".py", ".js", ".ts", ".ps1", ".sh" };
                        if (!allowed.Contains(extension)) throw new InvalidOperationException("扩展包包含不允许的文件类型：" + extension);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (var sourceStream = entry.Open()) using (var targetStream = File.Create(destination)) sourceStream.CopyTo(targetStream);
                    }
                }
                var manifests = Directory.EnumerateFiles(incomingRoot, "manifest.json", SearchOption.AllDirectories).ToArray();
                if (manifests.Length != 1) throw new InvalidOperationException("扩展 ZIP 必须且只能包含一个 manifest.json");
                return InstallExtensionFromManifest(workspace, manifests[0]);
            }
            finally { if (Directory.Exists(incomingRoot)) Directory.Delete(incomingRoot, true); }
        }

        private JObject InstallExtensionFromManifest(string workspace, string manifestPath)
        {
            var manifest = JsonUtil.Read(manifestPath, new JObject()) as JObject ?? new JObject();
            var type = ((string)manifest["type"] ?? "").Trim().ToLowerInvariant();
            if (type != "skill" && type != "agent") throw new InvalidOperationException("扩展 manifest.type 仅支持 skill 或 agent");
            var id = ((string)manifest["id"] ?? "").Trim().ToLowerInvariant();
            if (id.Length < 2 || id.Length > 64 || id.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')) throw new InvalidOperationException("扩展 id 只能使用字母、数字、- 和 _，长度 2-64");
            var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            var trust = ValidateSkinPackage(manifest, sourceRoot);
            var projectClaude = Path.Combine(workspace, ".claude"); Directory.CreateDirectory(projectClaude);
            if (type == "agent")
            {
                var sourceFile = new[] { Path.Combine(sourceRoot, "agent.md"), Path.Combine(sourceRoot, id + ".md") }.FirstOrDefault(File.Exists);
                if (sourceFile == null) throw new InvalidOperationException("Subagent 包必须包含 agent.md 或 " + id + ".md");
                var targetDir = Path.Combine(projectClaude, "agents"); Directory.CreateDirectory(targetDir);
                var target = ResolveInside(targetDir, id + ".md"); var incoming = target + ".incoming"; var backup = target + ".bak";
                File.Copy(sourceFile, incoming, true); if (File.Exists(target)) File.Replace(incoming, target, backup, true); else File.Move(incoming, target);
                JsonUtil.WriteAtomic(ResolveInside(targetDir, "." + id + ".workbench-package.json"), PackageMetadata(manifest, trust));
                return new JObject { ["ok"] = true, ["type"] = type, ["name"] = id, ["path"] = target, ["signatureStatus"] = trust["signatureStatus"] };
            }
            var skillFile = Path.Combine(sourceRoot, "SKILL.md");
            if (!File.Exists(skillFile)) throw new InvalidOperationException("Skill 包必须包含 SKILL.md");
            var skillsRoot = Path.Combine(projectClaude, "skills"); Directory.CreateDirectory(skillsRoot);
            var targetRoot = ResolveInside(skillsRoot, id); var installRoot = ResolveInside(skillsRoot, ".install-" + id + "-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(sourceRoot, installRoot, manifestPath);
            JsonUtil.WriteAtomic(Path.Combine(installRoot, ".workbench-package.json"), PackageMetadata(manifest, trust));
            var backupRoot = ResolveInside(skillsRoot, ".backup-" + id + "-" + Guid.NewGuid().ToString("N"));
            try { if (Directory.Exists(targetRoot)) Directory.Move(targetRoot, backupRoot); Directory.Move(installRoot, targetRoot); if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true); }
            catch { if (!Directory.Exists(targetRoot) && Directory.Exists(backupRoot)) Directory.Move(backupRoot, targetRoot); throw; }
            finally { if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true); }
            return new JObject { ["ok"] = true, ["type"] = type, ["name"] = id, ["path"] = Path.Combine(targetRoot, "SKILL.md"), ["signatureStatus"] = trust["signatureStatus"] };
        }

        private static JObject PackageMetadata(JObject manifest, JObject trust)
        {
            return new JObject { ["id"] = manifest["id"], ["type"] = manifest["type"], ["name"] = manifest["name"] ?? manifest["id"], ["version"] = manifest["version"] ?? "0.0.0", ["minAppVersion"] = manifest["minAppVersion"] ?? "", ["maxAppVersion"] = manifest["maxAppVersion"] ?? "", ["signatureStatus"] = trust["signatureStatus"], ["publisherThumbprint"] = trust["publisherThumbprint"], ["installedAt"] = ProviderStore.NowIso() };
        }

        private static void CopyDirectory(string sourceRoot, string targetRoot, string manifestPath)
        {
            Directory.CreateDirectory(targetRoot);
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase)) continue;
                var relative = file.Substring(sourceRoot.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                var target = ResolveInside(targetRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
            }
        }

        private JArray ListSkins()
        {
            Directory.CreateDirectory(SkinRoot);
            var result = new JArray();
            foreach (var file in Directory.EnumerateFiles(SkinRoot, "manifest.json", SearchOption.AllDirectories))
            {
                var manifest = JsonUtil.Read(file, new JObject()) as JObject;
                if (manifest == null || string.IsNullOrWhiteSpace((string)manifest["id"])) continue;
                manifest["imported"] = true;
                result.Add(manifest);
            }
            return result;
        }

        private async Task<JObject> ImportSkin(JObject body)
        {
            if (_window == null) throw new InvalidOperationException("当前窗口不可用");
            var requestedPath = ((string)body["path"] ?? "").Trim();
            var packagePath = requestedPath.Length > 0 ? requestedPath : await _window.SelectSkinPackageAsync();
            if (string.IsNullOrWhiteSpace(packagePath)) return new JObject { ["cancelled"] = true };
            packagePath = Path.GetFullPath(packagePath);
            if (!File.Exists(packagePath) || !string.Equals(Path.GetExtension(packagePath), ".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择 ZIP 格式的皮肤包");
            if (new FileInfo(packagePath).Length > 32 * 1024 * 1024) throw new InvalidOperationException("皮肤 ZIP 不能超过 32 MB");
            Directory.CreateDirectory(SkinRoot);
            var incomingRoot = Path.Combine(SkinRoot, ".incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(incomingRoot);
            try
            {
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    if (archive.Entries.Count == 0 || archive.Entries.Count > 100) throw new InvalidOperationException("皮肤 ZIP 必须包含 1-100 个文件");
                    long expanded = 0;
                    foreach (var entry in archive.Entries)
                    {
                        var relative = SafeZipRelative(entry.FullName);
                        expanded += entry.Length;
                        if (expanded > 40L * 1024 * 1024) throw new InvalidOperationException("皮肤 ZIP 解压后不能超过 40 MB");
                        var destination = ResolveInside(incomingRoot, relative);
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(destination); continue; }
                        var extension = Path.GetExtension(destination).ToLowerInvariant();
                        if (extension != ".json" && extension != ".css" && extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".webp") throw new InvalidOperationException("皮肤 ZIP 仅允许 JSON、CSS、PNG、JPG、WebP");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (var sourceStream = entry.Open()) using (var targetStream = File.Create(destination)) sourceStream.CopyTo(targetStream);
                    }
                }
                var manifests = Directory.EnumerateFiles(incomingRoot, "manifest.json", SearchOption.AllDirectories).ToArray();
                if (manifests.Length != 1) throw new InvalidOperationException("皮肤 ZIP 必须且只能包含一个 manifest.json");
                return InstallSkinFromManifest(manifests[0]);
            }
            finally
            {
                if (Directory.Exists(incomingRoot)) Directory.Delete(incomingRoot, true);
            }
        }

        private JObject InstallSkinFromManifest(string manifestPath)
        {
            var source = JsonUtil.Read(manifestPath, new JObject()) as JObject ?? new JObject();
            var id = ((string)source["id"] ?? Path.GetFileNameWithoutExtension(manifestPath)).Trim().ToLowerInvariant();
            if (id.Length < 2 || id.Length > 48 || id.Any(ch => !char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')) throw new InvalidOperationException("皮肤 id 只能使用字母、数字、- 和 _，长度 2-48");
            if (id == "fusion" || id == "claude" || id == "codex") throw new InvalidOperationException("皮肤 id 不能覆盖内置皮肤");
            var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
            var packageTrust = ValidateSkinPackage(source, sourceRoot);
            var css = ((string)source["css"] ?? "skin.css").Trim();
            var assets = source["assets"] as JObject ?? new JObject();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { css };
            foreach (var value in assets.Properties().Select(property => (string)property.Value).Where(value => !string.IsNullOrWhiteSpace(value))) files.Add(value);
            var targetRoot = Path.Combine(SkinRoot, id);
            var installRoot = Path.Combine(SkinRoot, ".install-" + id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(installRoot);
            foreach (var relative in files)
            {
                if (Path.IsPathRooted(relative) || relative.Contains("..")) throw new InvalidOperationException("皮肤资源必须使用 manifest 同目录下的相对路径");
                var sourceFile = Path.GetFullPath(Path.Combine(sourceRoot, relative));
                if (!sourceFile.StartsWith(sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourceFile)) throw new FileNotFoundException("找不到皮肤资源", relative);
                var extension = Path.GetExtension(sourceFile).ToLowerInvariant();
                if (extension != ".css" && extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".webp") throw new InvalidOperationException("皮肤资源仅支持 CSS、PNG、JPG、WebP");
                if (new FileInfo(sourceFile).Length > (extension == ".css" ? 512 * 1024 : 8 * 1024 * 1024)) throw new InvalidOperationException("皮肤资源文件过大：" + relative);
                if (extension == ".css")
                {
                    var content = File.ReadAllText(sourceFile, Encoding.UTF8);
                    if (content.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0 || content.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) >= 0 || content.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0 || content.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("皮肤 CSS 不能导入或请求外部资源");
                }
                var destination = ResolveInside(installRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(sourceFile, destination, true);
            }
            var normalized = new JObject
            {
                ["id"] = id, ["name"] = (string)source["name"] ?? id, ["description"] = (string)source["description"] ?? "",
                ["compatibility"] = (string)source["compatibility"] ?? "codex", ["agentTitle"] = (string)source["agentTitle"] ?? (string)source["name"] ?? id,
                ["css"] = css, ["assets"] = assets, ["imported"] = true,
                ["schemaVersion"] = (int?)source["schemaVersion"] ?? 1,
                ["minAppVersion"] = (string)source["minAppVersion"] ?? "",
                ["maxAppVersion"] = (string)source["maxAppVersion"] ?? "",
                ["signatureStatus"] = packageTrust["signatureStatus"],
                ["publisherThumbprint"] = packageTrust["publisherThumbprint"]
            };
            JsonUtil.WriteAtomic(Path.Combine(installRoot, "manifest.json"), normalized);
            var backupRoot = Path.Combine(SkinRoot, ".backup-" + id + "-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (Directory.Exists(targetRoot)) Directory.Move(targetRoot, backupRoot);
                Directory.Move(installRoot, targetRoot);
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
            }
            catch
            {
                if (!Directory.Exists(targetRoot) && Directory.Exists(backupRoot)) Directory.Move(backupRoot, targetRoot);
                throw;
            }
            finally { if (Directory.Exists(installRoot)) Directory.Delete(installRoot, true); }
            return normalized;
        }

        private static JObject ValidateSkinPackage(JObject manifest, string sourceRoot)
        {
            var current = ParseAppVersion(Program.AppContractVersion);
            var min = ParseAppVersion((string)manifest["minAppVersion"]);
            var max = ParseAppVersion((string)manifest["maxAppVersion"]);
            if (min != null && current.CompareTo(min) < 0) throw new InvalidOperationException("扩展包要求工作台版本不低于 " + min);
            if (max != null && current.CompareTo(max) > 0) throw new InvalidOperationException("扩展包只兼容工作台 " + max + " 或更早版本");
            var signature = manifest["signature"] as JObject;
            if (signature == null)
            {
                if (!((bool?)manifest["development"] ?? false))
                    throw new InvalidOperationException("生产扩展包必须包含受信任证书签名；本地开发包请在 manifest.json 明确设置 development: true");
                return new JObject { ["signatureStatus"] = "unsigned-development", ["publisherThumbprint"] = "" };
            }
            var algorithm = ((string)signature["algorithm"] ?? "RSA-SHA256").Trim();
            if (!string.Equals(algorithm, "RSA-SHA256", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("皮肤签名算法仅支持 RSA-SHA256");
            var thumbprint = NormalizeThumbprint((string)signature["thumbprint"]);
            var value = ((string)signature["value"] ?? "").Trim();
            var hashes = manifest["contentSha256"] as JObject;
            if (thumbprint.Length == 0 || value.Length == 0 || hashes == null || hashes.Count == 0) throw new InvalidOperationException("皮肤签名缺少 thumbprint、value 或 contentSha256");
            var declared = new HashSet<string>(hashes.Properties().Select(property => property.Name.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var actualFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(file => !string.Equals(Path.GetFileName(file), "manifest.json", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Substring(sourceRoot.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/')).ToArray();
            var undeclared = actualFiles.FirstOrDefault(file => !declared.Contains(file));
            if (undeclared != null) throw new InvalidOperationException("扩展包包含未纳入签名清单的文件：" + undeclared);
            foreach (var property in hashes.Properties())
            {
                var relative = property.Name.Replace('/', Path.DirectorySeparatorChar);
                var file = ResolveInside(sourceRoot, relative);
                if (!File.Exists(file)) throw new InvalidOperationException("签名清单中的文件不存在：" + property.Name);
                var actual = Sha256File(file);
                if (!string.Equals(actual, ((string)property.Value ?? "").Replace("-", "").Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("皮肤文件校验失败：" + property.Name);
            }
            var signedText = string.Join("\n", hashes.Properties().OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => property.Name.Replace('\\', '/') + "=" + ((string)property.Value ?? "").Replace("-", "").ToUpperInvariant()));
            var certificate = FindTrustedCertificate(thumbprint);
            if (certificate == null) throw new InvalidOperationException("找不到受信任的皮肤发布者证书：" + thumbprint);
            using (certificate)
            using (var rsa = certificate.GetRSAPublicKey())
            {
                if (rsa == null || !rsa.VerifyData(Encoding.UTF8.GetBytes(signedText), Convert.FromBase64String(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new InvalidOperationException("皮肤包签名无效或内容已被修改");
            }
            return new JObject { ["signatureStatus"] = "trusted", ["publisherThumbprint"] = thumbprint };
        }

        private static Version ParseAppVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var clean = value.Trim().Split('-')[0]; Version parsed; return Version.TryParse(clean, out parsed) ? parsed : null;
        }

        private static string NormalizeThumbprint(string value) { return new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant(); }
        private static string Sha256File(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
        private static X509Certificate2 FindTrustedCertificate(string thumbprint)
        {
            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            foreach (var name in new[] { StoreName.TrustedPeople, StoreName.Root })
            {
                try
                {
                    using (var store = new X509Store(name, location))
                    {
                        store.Open(OpenFlags.ReadOnly);
                        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, true);
                        if (found.Count > 0) return new X509Certificate2(found[0]);
                    }
                }
                catch { }
            }
            return null;
        }

        private JObject DeleteSkin(string id)
        {
            id = SafeId(id ?? "");
            if (id == "fusion" || id == "claude" || id == "codex") throw new InvalidOperationException("不能删除内置皮肤");
            var directory = Path.Combine(SkinRoot, id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            return new JObject { ["deleted"] = true, ["id"] = id };
        }

        private async Task ServeSkinAsset(HttpListenerResponse response, string tail)
        {
            var parts = (tail ?? "").Split(new[] { '/' }, 2);
            if (parts.Length != 2) { await Error(response, "皮肤资源地址无效", 404); return; }
            var root = Path.Combine(SkinRoot, SafeId(parts[0]));
            var file = ResolveInside(root, parts[1].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) { await Error(response, "找不到皮肤资源", 404); return; }
            var extension = Path.GetExtension(file).ToLowerInvariant();
            response.ContentType = extension == ".css" ? "text/css; charset=utf-8" : extension == ".png" ? "image/png" : extension == ".webp" ? "image/webp" : "image/jpeg";
            response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            var bytes = File.ReadAllBytes(file); response.ContentLength64 = bytes.Length; await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private JObject UpdateStatus()
        {
            var config = JsonUtil.Read(UpdateChannelFile, new JObject { ["channel"] = "stable", ["manifestUrl"] = "", ["allowUnsignedPreview"] = false }) as JObject ?? new JObject();
            config["currentVersion"] = Program.AppContractVersion;
            config["atomicUpdater"] = true;
            var currentExecutable = Process.GetCurrentProcess().MainModule.FileName;
            config["previousExecutable"] = Path.Combine(Path.GetDirectoryName(currentExecutable), Path.GetFileNameWithoutExtension(currentExecutable) + ".previous.exe");
            return config;
        }

        private JObject SaveUpdateConfig(JObject body)
        {
            var channel = ((string)body["channel"] ?? "stable").Trim().ToLowerInvariant();
            if (channel != "stable" && channel != "preview") throw new InvalidOperationException("更新通道仅支持 stable 或 preview");
            var manifestUrl = ((string)body["manifestUrl"] ?? "").Trim();
            if (manifestUrl.Length > 0) ValidateHttpsUrl(manifestUrl, "更新清单");
            var trusted = new JArray((body["trustedPublisherThumbprints"] as JArray ?? new JArray()).Select(value => NormalizeThumbprint((string)value)).Where(value => value.Length > 0).Distinct());
            var config = new JObject { ["channel"] = channel, ["manifestUrl"] = manifestUrl, ["allowUnsignedPreview"] = channel == "preview" && ((bool?)body["allowUnsignedPreview"] ?? false), ["trustedPublisherThumbprints"] = trusted, ["updatedAt"] = ProviderStore.NowIso() };
            JsonUtil.WriteAtomic(UpdateChannelFile, config); return UpdateStatus();
        }

        private async Task<JObject> CheckUpdate()
        {
            var config = UpdateStatus(); var manifestUrl = (string)config["manifestUrl"] ?? "";
            if (manifestUrl.Length == 0) return new JObject { ["configured"] = false, ["channel"] = config["channel"], ["currentVersion"] = Program.AppContractVersion, ["message"] = "尚未配置 HTTPS 发布清单" };
            ValidateHttpsUrl(manifestUrl, "更新清单");
            string text;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }) text = await client.GetStringAsync(manifestUrl);
            JObject manifest; try { manifest = JObject.Parse(text); } catch { throw new InvalidOperationException("更新清单不是有效 JSON"); }
            ValidateReleaseManifest(manifest, (string)config["channel"]);
            var current = ParseAppVersion(Program.AppContractVersion); var available = ParseAppVersion((string)manifest["version"]);
            return new JObject { ["configured"] = true, ["available"] = available != null && current != null && available.CompareTo(current) > 0, ["currentVersion"] = Program.AppContractVersion, ["channel"] = config["channel"], ["release"] = manifest };
        }

        private async Task<JObject> StageUpdate(JObject body)
        {
            var config = UpdateStatus(); var release = body["release"] as JObject ?? throw new InvalidOperationException("缺少 release 清单");
            ValidateReleaseManifest(release, (string)config["channel"]);
            var version = (string)release["version"]; var url = (string)release["url"]; var expected = ((string)release["sha256"] ?? "").Replace("-", "").ToUpperInvariant();
            var targetRoot = Path.Combine(AppPaths.Data, "updates", SafeId(version.Replace('.', '-'))); Directory.CreateDirectory(targetRoot);
            var staged = Path.Combine(targetRoot, EditionInfo.ExecutableName + ".incoming");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 256L * 1024 * 1024) throw new InvalidOperationException("更新包超过 256 MB");
                using (var source = await response.Content.ReadAsStreamAsync()) using (var target = File.Create(staged)) await source.CopyToAsync(target);
            }
            if (new FileInfo(staged).Length > 256L * 1024 * 1024 || !string.Equals(NativeUpdater.Sha256(staged), expected, StringComparison.OrdinalIgnoreCase)) { File.Delete(staged); throw new InvalidOperationException("更新包 SHA-256 校验失败"); }
            var signed = VerifySignedExecutable(staged, config["trustedPublisherThumbprints"] as JArray);
            if (!signed && ((string)config["channel"] == "stable" || !((bool?)config["allowUnsignedPreview"] ?? false))) { File.Delete(staged); throw new InvalidOperationException("更新包没有受信任的 Authenticode 签名"); }
            var ready = Path.Combine(targetRoot, EditionInfo.ExecutableName); if (File.Exists(ready)) File.Delete(ready); File.Move(staged, ready);
            return new JObject { ["staged"] = true, ["path"] = ready, ["version"] = version, ["sha256"] = expected, ["signed"] = signed, ["applyCommand"] = "--apply-update <staged> <target> <sha256>" };
        }

        private static void ValidateReleaseManifest(JObject manifest, string channel)
        {
            if ((int?)manifest["schemaVersion"] != 1) throw new InvalidOperationException("更新清单 schemaVersion 必须为 1");
            if (!string.Equals((string)manifest["channel"], channel, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("更新清单通道与当前设置不匹配");
            if (ParseAppVersion((string)manifest["version"]) == null) throw new InvalidOperationException("更新清单版本号无效");
            ValidateHttpsUrl((string)manifest["url"], "更新包");
            var sha = ((string)manifest["sha256"] ?? "").Replace("-", ""); if (sha.Length != 64 || sha.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidOperationException("更新清单 SHA-256 无效");
        }

        private static void ValidateHttpsUrl(string value, string label)
        {
            Uri uri; if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidOperationException(label + "必须使用有效 HTTPS 地址");
        }

        private static bool VerifySignedExecutable(string path, JArray trustedThumbprints)
        {
            try
            {
                var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); var chain = new X509Chain();
                var trusted = new HashSet<string>((trustedThumbprints ?? new JArray()).Select(value => NormalizeThumbprint((string)value)), StringComparer.OrdinalIgnoreCase);
                var chainValid = chain.Build(certificate); var publisherAllowed = trusted.Count == 0 || trusted.Contains(NormalizeThumbprint(certificate.Thumbprint));
                chain.Dispose(); certificate.Dispose(); return chainValid && publisherAllowed;
            }
            catch { return false; }
        }

        private JObject Usage(string workspace)
        {
            var normalized = NormalizeExistingDirectory(workspace, false);
            var byModel = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var byDay = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            long total = 0, input = 0, output = 0;
            var sessions = 0;
            foreach (var file in TranscriptFiles())
            {
                var summary = ParseTranscript(file, false);
                if (normalized.Length > 0 && !string.Equals((string)summary["workspace"], normalized, StringComparison.OrdinalIgnoreCase)) continue;
                sessions++;
                var usage = (JObject)summary["usage"];
                var itemInput = (long)usage["input_tokens"];
                var itemOutput = (long)usage["output_tokens"];
                var itemTotal = (long)usage["total_tokens"];
                input += itemInput; output += itemOutput; total += itemTotal;
                foreach (var modelUsage in summary["usageByModel"] as JArray ?? new JArray())
                {
                    var value = modelUsage as JObject;
                    if (value == null || string.IsNullOrWhiteSpace((string)value["name"])) continue;
                    Accumulate(byModel, (string)value["name"], (long)value["input"], (long)value["output"], (long)value["total"]);
                }
                var day = ((string)summary["updatedAt"] ?? "").Take(10).Aggregate("", (value, ch) => value + ch);
                if (day.Length > 0) Accumulate(byDay, day, itemInput, itemOutput, itemTotal);
            }
            return new JObject
            {
                ["sessions"] = sessions, ["input"] = input, ["output"] = output, ["total"] = total,
                ["byModel"] = new JArray(byModel.Select(pair => pair.Value)), ["byDay"] = new JArray(byDay.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            };
        }

        private static JObject Health(string workspace)
        {
            var root = NormalizeExistingDirectory(workspace, false);
            var claude = Path.Combine(AppPaths.ClaudeRoot, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            var git = Run("git.exe", "--version", root.Length > 0 ? root : AppPaths.Workspace, 5000);
            return new JObject
            {
                ["workspace"] = root, ["workspaceExists"] = Directory.Exists(root), ["claudePath"] = claude,
                ["claudeExists"] = File.Exists(claude), ["git"] = git.Output.Trim(), ["gitAvailable"] = git.ExitCode == 0,
                ["transcriptRoot"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"),
                ["webViewRuntime"] = true, ["terminalRuntime"] = "Windows ConPTY", ["durableJobState"] = true,
                ["eventReplay"] = true, ["skinPackage"] = "zip", ["processModel"] = "host-ui-worker-native", ["version"] = Program.AppContractVersion
            };
        }

        private static JObject SaveClipboardImage(JObject body)
        {
            var data = ((string)body["data"] ?? "").Trim();
            var comma = data.IndexOf(',');
            if (comma >= 0) data = data.Substring(comma + 1);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch { throw new InvalidOperationException("剪贴板图片数据无效"); }
            if (bytes.Length == 0 || bytes.Length > 20 * 1024 * 1024) throw new InvalidOperationException("剪贴板图片必须小于 20 MB");
            var root = Path.Combine(AppPaths.Data, "clipboard");
            Directory.CreateDirectory(root);
            var extension = ((string)body["mime"] ?? "").IndexOf("jpeg", StringComparison.OrdinalIgnoreCase) >= 0 ? ".jpg" : ".png";
            var file = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
            File.WriteAllBytes(file, bytes);
            return new JObject { ["path"] = file, ["size"] = bytes.Length };
        }

        private JObject StartTerminal(string workspace)
        {
            var root = NormalizeExistingDirectory(workspace, true);
            lock (_terminalGate)
            {
                var existing = _terminals.Values.FirstOrDefault(value => value.Alive && string.Equals(value.Workspace, root, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return new JObject { ["id"] = existing.Id, ["workspace"] = existing.Workspace, ["reused"] = true };
                var session = new ConPtyTerminalSession(root);
                _terminals[session.Id] = session;
                return new JObject { ["id"] = session.Id, ["workspace"] = session.Workspace };
            }
        }

        private JObject WriteTerminal(string id, string command, bool submit)
        {
            ConPtyTerminalSession session;
            lock (_terminalGate) { if (!_terminals.TryGetValue(id ?? "", out session)) throw new InvalidOperationException("终端会话不存在"); }
            if (submit) session.Write(command ?? ""); else session.WriteRaw(command ?? "");
            return new JObject { ["ok"] = true, ["id"] = session.Id };
        }

        private JObject ResizeTerminal(string id, int columns, int rows)
        {
            ConPtyTerminalSession session;
            lock (_terminalGate) { if (!_terminals.TryGetValue(id ?? "", out session)) throw new InvalidOperationException("终端会话不存在"); }
            columns = Math.Max(40, Math.Min(300, columns)); rows = Math.Max(12, Math.Min(120, rows));
            session.Resize((short)columns, (short)rows);
            return new JObject { ["ok"] = true, ["id"] = session.Id, ["columns"] = columns, ["rows"] = rows };
        }

        private JObject PollTerminal(string id)
        {
            ConPtyTerminalSession session;
            lock (_terminalGate) { if (!_terminals.TryGetValue(id ?? "", out session)) return new JObject { ["alive"] = false, ["output"] = "" }; }
            return new JObject { ["id"] = session.Id, ["alive"] = session.Alive, ["output"] = session.Read() };
        }

        private JObject StopTerminal(string id)
        {
            ConPtyTerminalSession session = null;
            lock (_terminalGate) { if (_terminals.TryGetValue(id ?? "", out session)) _terminals.Remove(id); }
            if (session != null) session.Dispose();
            return new JObject { ["stopped"] = session != null };
        }

        public void StopAllTerminals()
        {
            ConPtyTerminalSession[] sessions;
            lock (_terminalGate) { sessions = _terminals.Values.ToArray(); _terminals.Clear(); }
            foreach (var session in sessions) session.Dispose();
        }

        private string CheckpointRoot(string sessionId)
        {
            return Path.Combine(AppPaths.Data, "checkpoints", SafeId(sessionId));
        }

        private JArray ListCheckpoints(string sessionId)
        {
            var root = CheckpointRoot(sessionId);
            if (!Directory.Exists(root)) return new JArray();
            return new JArray(Directory.EnumerateFiles(root, "*.jsonl").OrderByDescending(file => file).Select(file => new JObject
            {
                ["id"] = Path.GetFileNameWithoutExtension(file), ["label"] = ReadCheckpointLabel(file),
                ["createdAt"] = File.GetCreationTimeUtc(file).ToString("o"), ["size"] = new FileInfo(file).Length
            }));
        }

        private JObject CreateCheckpoint(string sessionId, string workspace, string label)
        {
            var file = FindTranscript(SafeId(sessionId), workspace ?? "");
            if (file == null) throw new FileNotFoundException("当前会话还没有可保存的 transcript");
            var root = CheckpointRoot(sessionId); Directory.CreateDirectory(root);
            var id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var target = Path.Combine(root, id + ".jsonl");
            File.Copy(file, target, false);
            File.WriteAllText(target + ".meta", string.IsNullOrWhiteSpace(label) ? "手动检查点" : label.Trim(), new UTF8Encoding(false));
            return new JObject { ["id"] = id, ["label"] = ReadCheckpointLabel(target), ["createdAt"] = DateTime.UtcNow.ToString("o") };
        }

        private JObject RestoreCheckpoint(string sessionId, string workspace, string checkpointId, bool fork)
        {
            var source = Path.Combine(CheckpointRoot(sessionId), SafeId(checkpointId) + ".jsonl");
            if (!File.Exists(source)) throw new FileNotFoundException("检查点不存在");
            var current = FindTranscript(SafeId(sessionId), workspace ?? "");
            if (current == null) throw new FileNotFoundException("当前 transcript 不存在");
            if (!fork)
            {
                File.Copy(current, current + ".before-restore-" + DateTime.Now.ToString("yyyyMMddHHmmss"), false);
                File.Copy(source, current, true);
                return new JObject { ["restored"] = true, ["sessionId"] = sessionId, ["forked"] = false };
            }
            var newId = Guid.NewGuid().ToString();
            var target = Path.Combine(Path.GetDirectoryName(current), newId + ".jsonl");
            var text = File.ReadAllText(source, Encoding.UTF8).Replace(sessionId, newId);
            File.WriteAllText(target, text, new UTF8Encoding(false));
            _eventStore.UpsertTask(newId, workspace ?? AppPaths.Workspace, "从检查点创建的分支", "draft", false, "{}");
            var relation = _eventStore.RecordFork(sessionId, newId, checkpointId, new JObject { ["permissionInheritance"] = "restrict-only", ["sourceTranscript"] = current });
            return new JObject { ["restored"] = true, ["sessionId"] = newId, ["forked"] = true, ["relation"] = relation };
        }

        private static string ReadCheckpointLabel(string file)
        {
            try { var meta = file + ".meta"; return File.Exists(meta) ? File.ReadAllText(meta, Encoding.UTF8) : "检查点"; }
            catch { return "检查点"; }
        }

        private static void Accumulate(IDictionary<string, JObject> map, string key, long input, long output, long total)
        {
            JObject value;
            if (!map.TryGetValue(key, out value)) { value = new JObject { ["name"] = key, ["input"] = 0L, ["output"] = 0L, ["total"] = 0L }; map[key] = value; }
            value["input"] = (long)value["input"] + input; value["output"] = (long)value["output"] + output; value["total"] = (long)value["total"] + total;
        }

        private static JObject MergeSettings(params string[] files)
        {
            var result = new JObject();
            foreach (var file in files)
            {
                var source = JsonUtil.Read(file, new JObject()) as JObject;
                if (source != null) result.Merge(source, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            }
            return result;
        }

        private static IEnumerable<string> FindClaudeMd(string root)
        {
            try { return Directory.EnumerateFiles(root, "CLAUDE.md", SearchOption.AllDirectories).Where(path => path.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static CommandResult Run(string file, string arguments, string workingDirectory, int timeout)
        {
            lock (typeof(WorkbenchApi))
            {
                try
                {
                    var info = new ProcessStartInfo(file, arguments)
                    {
                        WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : AppPaths.Workspace,
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                    };
                    using (var process = Process.Start(info))
                    {
                        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                        if (!process.WaitForExit(timeout)) { try { process.Kill(); } catch { } return new CommandResult { ExitCode = -1, Error = "命令执行超时" }; }
                        Task.WaitAll(new Task[] { output, error }, 3000);
                        return new CommandResult { ExitCode = process.ExitCode, Output = output.Result ?? "", Error = error.Result ?? "" };
                    }
                }
                catch (Exception error) { return new CommandResult { ExitCode = -1, Error = error.Message }; }
            }
        }

        private static IEnumerable<string> ReadLinesShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null) yield return line;
            }
        }

        private static string ExtractText(JToken content)
        {
            if (content == null) return "";
            if (content.Type == JTokenType.String) return (string)content ?? "";
            var array = content as JArray;
            if (array == null) return "";
            return string.Join("\n", array.OfType<JObject>().Where(block => (string)block["type"] == "text").Select(block => (string)block["text"]).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string NormalizeExistingDirectory(string value, bool required)
        {
            var candidate = string.IsNullOrWhiteSpace(value) ? (required ? AppPaths.Workspace : "") : Path.GetFullPath(value);
            if (required && !Directory.Exists(candidate)) throw new DirectoryNotFoundException("工作区不存在：" + candidate);
            return candidate;
        }

        private static string ResolveInside(string root, string relative)
        {
            var candidate = Path.GetFullPath(Path.IsPathRooted(relative ?? "") ? relative : Path.Combine(root, relative ?? ""));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("路径超出当前工作区");
            return candidate;
        }

        internal static string SafeZipRelative(string fullName)
        {
            var value = (fullName ?? "").Replace('/', Path.DirectorySeparatorChar);
            var normalized = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (value.Length == 0 || value[0] == Path.DirectorySeparatorChar || value[0] == Path.AltDirectorySeparatorChar ||
                normalized.Length == 0 || value.IndexOf(':') >= 0 || Path.IsPathRooted(value) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part == ".." || part.Length == 0))
                throw new InvalidOperationException("皮肤 ZIP 包含不安全路径");
            return normalized;
        }

        private static string Relative(string root, string path)
        {
            var rootUri = new Uri(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string SafeId(string value)
        {
            var result = new string((value ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
            if (result.Length == 0 || result.Length > 100) throw new InvalidOperationException("无效会话 ID");
            return result;
        }

        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }
        private static string OneLine(string value, int max) { return Limit((value ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), max); }
        private static string Limit(string value, int max) { return value == null ? "" : value.Length <= max ? value : value.Substring(0, max); }
        private static string JoinText(string left, string right) { return string.IsNullOrWhiteSpace(left) ? right : string.IsNullOrWhiteSpace(right) ? left : left + "\n" + right; }

        private static async Task WriteJson(HttpListenerResponse response, JToken token, int status = 200)
        {
            var bytes = Encoding.UTF8.GetBytes((token ?? JValue.CreateNull()).ToString(Formatting.None));
            response.StatusCode = status; response.ContentType = "application/json; charset=utf-8"; response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static Task Error(HttpListenerResponse response, string message, int status) { return WriteJson(response, new JObject { ["error"] = message }, status); }

        private sealed class CommandResult { public int ExitCode; public string Output = ""; public string Error = ""; }

        private sealed class LegacyRedirectedTerminalSession : IDisposable
        {
            private readonly object _gate = new object();
            private readonly Queue<string> _output = new Queue<string>();
            private readonly Process _process;
            public readonly string Id = Guid.NewGuid().ToString("N");
            public readonly string Workspace;
            public bool Alive { get { try { return !_process.HasExited; } catch { return false; } } }

            public LegacyRedirectedTerminalSession(string workspace)
            {
                Workspace = workspace;
                _process = new Process { StartInfo = new ProcessStartInfo("cmd.exe", "/Q /K")
                {
                    WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }, EnableRaisingEvents = true };
                _process.OutputDataReceived += (sender, args) => { if (args.Data != null) Enqueue(args.Data + Environment.NewLine); };
                _process.ErrorDataReceived += (sender, args) => { if (args.Data != null) Enqueue(args.Data + Environment.NewLine); };
                _process.Exited += (sender, args) => Enqueue("[Shell 已退出]" + Environment.NewLine);
                if (!_process.Start()) throw new InvalidOperationException("无法启动持久终端");
                _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
                Enqueue("Claude Workbench Persistent Shell" + Environment.NewLine + workspace + "> " );
            }
            private void Enqueue(string value) { lock (_gate) { _output.Enqueue(value); while (_output.Count > 5000) _output.Dequeue(); } }
            public string Read() { lock (_gate) { var result = new StringBuilder(); while (_output.Count > 0) result.Append(_output.Dequeue()); return result.ToString(); } }
            public void Write(string command)
            {
                if (!Alive) throw new InvalidOperationException("终端进程已经退出");
                Enqueue("> " + command + Environment.NewLine);
                _process.StandardInput.WriteLine(command); _process.StandardInput.Flush();
            }
            public void Dispose() { try { if (!_process.HasExited) _process.Kill(); } catch { } try { _process.Dispose(); } catch { } }
        }
    }
}

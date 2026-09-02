using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class ApiServer : IDisposable
    {
        public const int ProtocolVersion = 2;
        private const int MaxConcurrentWorkers = 4;
        private const int MaxIdleWorkers = 4;
        private static readonly TimeSpan IdleWorkerRetention = TimeSpan.FromMinutes(10);
        private const int MaximumModelListBytes = 8 * 1024 * 1024;
        private const int MaximumImageResponseBytes = 40 * 1024 * 1024;
        private const int MaximumDownloadedImageBytes = 24 * 1024 * 1024;
        private static readonly int MaximumRequestBodyBytes = ReadSizeSetting("CLAUDE_GUI_MAX_REQUEST_BODY_BYTES", 64 * 1024 * 1024);
        private static readonly TimeSpan RequestBodyIdleTimeout = ReadTimeoutSetting("CLAUDE_GUI_REQUEST_BODY_IDLE_TIMEOUT_SECONDS", 30);
        private static readonly TimeSpan ModelDiscoveryTimeout = ReadTimeoutSetting("CLAUDE_GUI_MODEL_DISCOVERY_TIMEOUT_SECONDS", 30);
        private static readonly TimeSpan ImageRequestTimeout = ReadTimeoutSetting("CLAUDE_GUI_IMAGE_TIMEOUT_SECONDS", 180);
        private static int TestQueueLoopFailureInjected;
        private static int TestJobLoopFailureInjected;
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromMinutes(10) };

        private readonly object _lifecycleGate = new object();
        private readonly ProviderStore _providers;
        private readonly WorkbenchApi _workbench;
        private readonly PermissionBroker _permissions;
        private readonly AgentEventStore _eventStore;
        private readonly ConcurrentDictionary<string, Job> _jobs = new ConcurrentDictionary<string, Job>();
        private readonly string _secret = LoadOrCreateHostSecret();
        private HttpListener _listener;
        private CancellationTokenSource _cancel;
        private Task _acceptLoop;
        private NativeHost _window;
        private int _port;
        private Timer _schedulerTimer;
        private Timer _queueTimer;
        private Timer _jobTimer;
        private int _schedulerChecking;
        private int _queueChecking;
        private int _jobChecking;
        private int _updateMaintenance;
        private readonly object _activityGate = new object();
        private long _activityRevision = 1;
        private TaskCompletionSource<long> _activityChanged = NewActivitySignal();

        public event Action<bool> StateChanged;
        public bool IsRunning { get; private set; }
        public string BaseUrl { get { return "http://127.0.0.1:" + _port + "/"; } }
        public bool HasActiveJobs { get { return _jobs.Values.Any(job => job.IsActive); } }
        public bool IsUpdateMaintenance { get { return Volatile.Read(ref _updateMaintenance) != 0; } }

        public ApiServer()
        {
            _eventStore = new AgentEventStore();
            _eventStore.InitializeAndMigrate();
            _providers = new ProviderStore();
            try { _eventStore.PruneProviderHealth(_providers.AllPublic().OfType<JObject>().Select(item => (string)item["id"])); }
            catch (Exception error) { CrashLog.Handled("ProviderHealthPrune", error); }
            _workbench = new WorkbenchApi(_eventStore, TryBeginUpdateMaintenance, CancelUpdateMaintenance, () => IsUpdateMaintenance);
            _permissions = new PermissionBroker(_eventStore);
            _permissions.Changed += TouchActivity;
        }
        public void AttachWindow(NativeHost window) { _window = window; _workbench.AttachWindow(window); _permissions.AttachWindow(window); }

        private bool TryBeginUpdateMaintenance()
        {
            if (HasActiveJobs || Interlocked.CompareExchange(ref _updateMaintenance, 1, 0) != 0) return false;
            if (HasActiveJobs)
            {
                Interlocked.Exchange(ref _updateMaintenance, 0);
                return false;
            }
            WriteRuntimeState("update-maintenance");
            return true;
        }

        private void CancelUpdateMaintenance()
        {
            Interlocked.Exchange(ref _updateMaintenance, 0);
            if (IsRunning) WriteRuntimeState("running");
        }

        public void Start()
        {
            lock (_lifecycleGate)
            {
                if (IsRunning) return;
                if (_port == 0) _port = FreePort();
                _listener = new HttpListener();
                _listener.Prefixes.Add(BaseUrl);
                try { _listener.Start(); }
                catch (HttpListenerException)
                {
                    _port = FreePort();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(BaseUrl);
                    _listener.Start();
                }
                _cancel = new CancellationTokenSource();
                RestoreActiveJobs();
                IsRunning = true;
                NativeMetrics.RecordHostStart(File.Exists(Path.Combine(AppPaths.Data, "native-metrics.json")));
                NativeMetrics.ReconcileActiveRuns(_jobs.Values.Where(job => job.Kind == "chat" && job.IsActive).Select(job => job.Id));
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancel.Token));
                _jobTimer = new Timer(state => ReconcileActiveJobs(), null, TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(450));
                _schedulerTimer = new Timer(async state => await CheckSchedulesAsync(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));
                _queueTimer = new Timer(async state => await CheckTaskQueueAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                WriteRuntimeState("running");
            }
            var handler = StateChanged;
            if (handler != null) handler(true);
        }

        public void Stop(bool stopJobs)
        {
            lock (_lifecycleGate)
            {
                if (stopJobs)
                {
                    foreach (var job in _jobs.Values.Where(value => value.IsActive).ToArray()) StopJob(job);
                    foreach (var job in _jobs.Values.Where(value => !value.IsActive && value.Persistent && JobProcessAlive(value)).ToArray()) RetireIdleWorker(job);
                    _workbench.StopAllTerminals();
                }
                if (!IsRunning) return;
                IsRunning = false;
                try { _cancel.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
                try { if (_schedulerTimer != null) _schedulerTimer.Dispose(); } catch { }
                _schedulerTimer = null;
                try { if (_queueTimer != null) _queueTimer.Dispose(); } catch { }
                _queueTimer = null;
                try { if (_jobTimer != null) _jobTimer.Dispose(); } catch { }
                _jobTimer = null;
                NativeMetrics.Flush();
                WriteRuntimeState("stopped");
            }
            var handler = StateChanged;
            if (handler != null) handler(false);
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    var requestTask = Task.Run(() => HandleContextAsync(context));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { if (IsRunning) await Task.Delay(80); else break; }
                catch (Exception error) { CrashLog.Handled("HttpAccept", error); await Task.Delay(100); }
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            context.Response.KeepAlive = false;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            NativeMetrics.RequestScope metrics = null;
            Exception routeError = null;
            var routePath = "";
            try
            {
                var path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath);
                routePath = path;
                var method = context.Request.HttpMethod.ToUpperInvariant();
                metrics = NativeMetrics.BeginRequest(method, path);
                if (path.StartsWith("/api/", StringComparison.Ordinal) && context.Request.Headers["X-Desktop-Secret"] != _secret)
                {
                    await WriteJsonAsync(context.Response, new JObject { ["error"] = "Forbidden" }, 403);
                    return;
                }
                if (path.StartsWith("/api/", StringComparison.Ordinal) && context.Request.Headers["X-Workbench-Protocol"] != ProtocolVersion.ToString())
                {
                    await WriteJsonAsync(context.Response, new JObject { ["error"] = "IPC protocol mismatch", ["expectedProtocol"] = ProtocolVersion }, 426);
                    return;
                }
                if (await _permissions.HandleAsync(context, path, method)) return;
                if (method == "GET" && path == "/") { await ServeResource(context.Response, "static.index.html", "text/html; charset=utf-8", false); return; }
                if (method == "GET" && path.StartsWith("/static/", StringComparison.Ordinal))
                {
                    await ServeStatic(context.Response, path.Substring(8)); return;
                }
                if (path.StartsWith("/adapter/", StringComparison.Ordinal)) { await HandleAdapter(context, path, method); return; }
                if (method == "GET" && path == "/api/bootstrap") { await Bootstrap(context.Response); return; }
                if (method == "POST" && path == "/api/settings") { JsonUtil.WriteAtomic(AppPaths.SettingsFile, await ReadBody(context.Request)); await Ok(context.Response); return; }
                if (method == "GET" && path == "/api/providers") { await WriteJsonAsync(context.Response, _providers.AllPublic()); return; }
                if (method == "POST" && path == "/api/providers") { await SaveProvider(context); return; }
                if (method == "GET" && path == "/api/providers/health") { await WriteJsonAsync(context.Response, _eventStore.ListProviderHealth(context.Request.QueryString["providerId"] ?? "")); return; }
                if (method == "POST" && path.StartsWith("/api/providers/health/reset/", StringComparison.Ordinal))
                {
                    _eventStore.DeleteProviderHealth(SafeId(path.Substring("/api/providers/health/reset/".Length))); await Ok(context.Response); return;
                }
                if (method == "DELETE" && path.StartsWith("/api/providers/", StringComparison.Ordinal))
                {
                    var providerId = path.Substring("/api/providers/".Length); var deleted = _providers.Delete(providerId);
                    if (deleted) _eventStore.DeleteProviderHealth(providerId);
                    await WriteJsonAsync(context.Response, new JObject { ["deleted"] = deleted }); return;
                }
                if (method == "POST" && path == "/api/providers/discover") { await DiscoverModels(context); return; }
                if (method == "POST" && path == "/api/providers/probe") { await ProbeProvider(context); return; }
                if (method == "POST" && path == "/api/providers/validate-model") { await ValidateProviderModel(context); return; }
                if (method == "POST" && path == "/api/sessions") { JsonUtil.WriteAtomic(AppPaths.SessionsFile, await ReadBody(context.Request)); await Ok(context.Response); return; }
                if (method == "DELETE" && path.StartsWith("/api/sessions/", StringComparison.Ordinal) && !path.EndsWith("/messages", StringComparison.Ordinal))
                {
                    var id = SafeId(path.Substring("/api/sessions/".Length));
                    await DeleteSession(context, id); return;
                }
                if (path.StartsWith("/api/sessions/", StringComparison.Ordinal) && path.EndsWith("/messages", StringComparison.Ordinal))
                {
                    var id = SafeId(path.Substring("/api/sessions/".Length, path.Length - "/api/sessions/".Length - "/messages".Length));
                    var file = Path.Combine(AppPaths.Messages, id + ".json");
                    if (method == "GET") await WriteJsonAsync(context.Response, JsonUtil.Read(file, new JArray()));
                    else if (method == "POST") { JsonUtil.WriteAtomic(file, await ReadBody(context.Request)); await Ok(context.Response); }
                    else await NotFound(context.Response);
                    return;
                }
                if (method == "POST" && path == "/api/chat/context-preview") { await PreviewContext(context); return; }
                if (method == "POST" && path == "/api/chat/start") { await StartChat(context); return; }
                if (method == "GET" && path == "/api/chat/runs") { await WriteJsonAsync(context.Response, RunCenter()); return; }
                if (method == "GET" && path == "/api/workbench/activity") { await WaitForActivity(context); return; }
                if (method == "POST" && path == "/api/agents/children/start") { await StartChildAgent(context); return; }
                if (method == "GET" && path == "/api/agents/children") { await ListChildAgents(context); return; }
                if (method == "POST" && path.StartsWith("/api/agents/children/handoff/", StringComparison.Ordinal)) { await AcceptChildHandoff(context, path.Substring("/api/agents/children/handoff/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/agents/children/retry/", StringComparison.Ordinal)) { await RetryChildAgent(context, path.Substring("/api/agents/children/retry/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/chat/steer/", StringComparison.Ordinal)) { await SteerChat(context, path.Substring("/api/chat/steer/".Length)); return; }
                if (method == "GET" && path.StartsWith("/api/chat/poll/", StringComparison.Ordinal)) { await PollChat(context, path.Substring("/api/chat/poll/".Length)); return; }
                if (method == "GET" && path.StartsWith("/api/chat/tools/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, _eventStore.ReadToolCalls(SafeId(path.Substring("/api/chat/tools/".Length)))); return; }
                if (method == "GET" && path.StartsWith("/api/chat/context/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, _eventStore.ContextReport(SafeId(path.Substring("/api/chat/context/".Length)))); return; }
                if (method == "GET" && path.StartsWith("/api/chat/artifacts/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, _eventStore.ListArtifacts(SafeId(path.Substring("/api/chat/artifacts/".Length)))); return; }
                if (method == "GET" && path.StartsWith("/api/chat/evidence/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, RunEvidence(SafeId(path.Substring("/api/chat/evidence/".Length)))); return; }
                if (method == "POST" && path.StartsWith("/api/chat/stop/", StringComparison.Ordinal)) { await StopChat(context.Response, path.Substring("/api/chat/stop/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/chat/pause/", StringComparison.Ordinal)) { await PauseChat(context.Response, path.Substring("/api/chat/pause/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/chat/resume/", StringComparison.Ordinal)) { await ResumeChat(context.Response, path.Substring("/api/chat/resume/".Length)); return; }
                if (method == "GET" && path == "/api/task-queue") { await ListTaskQueue(context); return; }
                if (method == "POST" && path == "/api/task-queue") { await EnqueueTask(context); return; }
                if (method == "POST" && path.StartsWith("/api/task-queue/cancel/", StringComparison.Ordinal))
                {
                    var cancelled = _eventStore.CancelQueue(SafeId(path.Substring("/api/task-queue/cancel/".Length)));
                    if (cancelled) TouchActivity();
                    await WriteJsonAsync(context.Response, new JObject { ["cancelled"] = cancelled }); return;
                }
                if (method == "POST" && path.StartsWith("/api/task-queue/prioritize/", StringComparison.Ordinal)) { await PrioritizeTaskQueue(context, path.Substring("/api/task-queue/prioritize/".Length)); return; }
                if (method == "POST" && path == "/api/image/start") { await StartImage(context); return; }
                if (method == "GET" && path.StartsWith("/api/image/poll/", StringComparison.Ordinal)) { await PollImage(context.Response, path.Substring("/api/image/poll/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/image/stop/", StringComparison.Ordinal)) { await StopImage(context.Response, path.Substring("/api/image/stop/".Length)); return; }
                if (method == "GET" && path.StartsWith("/api/image/file/", StringComparison.Ordinal)) { await SendImage(context.Response, path.Substring("/api/image/file/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/open/", StringComparison.Ordinal)) { await OpenTarget(context.Response, path.Substring("/api/open/".Length)); return; }
                if (method == "POST" && path == "/api/files/select")
                {
                    var files = _window == null ? new string[0] : await _window.SelectFilesAsync();
                    await WriteJsonAsync(context.Response, new JObject { ["files"] = new JArray(files) }); return;
                }
                if (method == "GET" && path == "/api/files/preview") { await PreviewFile(context); return; }
                if (method == "POST" && path == "/api/files/open") { await OpenLocalFile(context); return; }
                if (method == "POST" && path == "/api/files/export-text") { await ExportText(context); return; }
                if (path.StartsWith("/api/workbench/", StringComparison.Ordinal) && await _workbench.HandleAsync(context, path, method)) return;
                await NotFound(context.Response);
            }
            catch (Exception error)
            {
                if (IsClientDisconnect(error)) return;
                var status = RequestErrorStatus(error, routePath);
                if (status >= 500)
                {
                    routeError = error;
                    CrashLog.Handled("HttpRoute", error);
                }
                try { await WriteJsonAsync(context.Response, new JObject { ["error"] = SecretRedactor.Redact(error.Message), ["code"] = status >= 500 ? "internal_error" : "request_invalid" }, status); } catch { }
            }
            finally
            {
                NativeMetrics.EndRequest(metrics, context.Response.StatusCode, routeError);
                try { context.Response.OutputStream.Close(); } catch { }
            }
        }

        private static bool IsClientDisconnect(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
            {
                if (current is ObjectDisposedException || current is OperationCanceledException) return true;
                var listener = current as HttpListenerException;
                if (listener != null && (listener.ErrorCode == 64 || listener.ErrorCode == 995 || listener.ErrorCode == 1229 || listener.ErrorCode == 1236)) return true;
                var socket = current as SocketException;
                if (socket != null && (socket.SocketErrorCode == SocketError.ConnectionAborted || socket.SocketErrorCode == SocketError.ConnectionReset ||
                    socket.SocketErrorCode == SocketError.NotConnected || socket.SocketErrorCode == SocketError.OperationAborted || socket.SocketErrorCode == SocketError.Shutdown)) return true;
                var io = current as IOException;
                if (io != null)
                {
                    var code = io.HResult & 0xFFFF;
                    if (code == 64 || code == 995 || code == 1229 || code == 1236) return true;
                }
            }
            return false;
        }

        private static int RequestErrorStatus(Exception error, string path)
        {
            if (error is RequestBodyTooLargeException) return 413;
            if (error is RequestBodyTimeoutException) return 408;
            if (error is FileNotFoundException || error is DirectoryNotFoundException) return 404;
            if (error is UnauthorizedAccessException) return 403;
            if (error is ArgumentException || error is FormatException || error is JsonException) return 400;
            if (error is InvalidOperationException &&
                ((path ?? "").StartsWith("/api/workbench/", StringComparison.Ordinal) ||
                 (path ?? "").StartsWith("/api/providers", StringComparison.Ordinal) ||
                 (path ?? "").StartsWith("/api/files/", StringComparison.Ordinal) ||
                 (path ?? "").StartsWith("/api/sessions/", StringComparison.Ordinal)))
            {
                var message = error.Message ?? "";
                if (!message.StartsWith("SQLite ", StringComparison.OrdinalIgnoreCase) &&
                    message.IndexOf("Win32=", StringComparison.OrdinalIgnoreCase) < 0 &&
                    message.IndexOf("非法 Job 状态迁移", StringComparison.OrdinalIgnoreCase) < 0) return 400;
            }
            return 500;
        }

        private async Task Bootstrap(HttpListenerResponse response)
        {
            var fallback = new JObject
            {
                ["providerId"] = "deepseek-default", ["model"] = "deepseek-v4-pro[1m]", ["effort"] = "max", ["permissionMode"] = "agent", ["maxTurns"] = 100, ["workerHarness"] = "auto"
            };
            await WriteJsonAsync(response, new JObject
            {
                ["providers"] = _providers.AllPublic(), ["sessions"] = JsonUtil.Read(AppPaths.SessionsFile, new JArray()),
                ["settings"] = JsonUtil.Read(AppPaths.SettingsFile, fallback), ["workspace"] = AppPaths.Workspace,
                ["version"] = Program.AppContractVersion, ["edition"] = new JObject { ["id"] = EditionInfo.Id, ["productName"] = EditionInfo.ProductName, ["openSource"] = EditionInfo.IsOpenSource }, ["backend"] = "C#/.NET native host", ["trayMode"] = true, ["maxConcurrentWorkers"] = MaxConcurrentWorkers,
                ["persistence"] = _eventStore.Health(), ["providerHealth"] = _eventStore.ListProviderHealth(),
                ["activeJobs"] = RunCenter()
            });
        }

        private JArray RunCenter()
        {
            return new JArray(_jobs.Values.Where(job => job.Kind == "chat" && job.IsActive)
                .OrderByDescending(job => job.StartedAtUtc)
                .Select(RunSummary));
        }

        private async Task WaitForActivity(HttpListenerContext context)
        {
            long after; if (!long.TryParse(context.Request.QueryString["after"], out after) || after < 0) after = 0;
            int timeoutMs; if (!int.TryParse(context.Request.QueryString["timeoutMs"], out timeoutMs)) timeoutMs = 15000;
            timeoutMs = Math.Max(1000, Math.Min(20000, timeoutMs));
            var sessionId = (context.Request.QueryString["sessionId"] ?? "").Trim();
            if (sessionId.Length > 0)
            {
                var safe = SafeId(sessionId);
                if (!string.Equals(safe, sessionId, StringComparison.Ordinal)) throw new ArgumentException("无效的活动会话 ID");
            }

            Task<long> changeTask = null; long revision;
            lock (_activityGate)
            {
                revision = _activityRevision;
                if (after == revision) changeTask = _activityChanged.Task;
            }
            if (changeTask != null) await Task.WhenAny(changeTask, Task.Delay(timeoutMs));
            lock (_activityGate) revision = _activityRevision;
            await WriteJsonAsync(context.Response, new JObject
            {
                ["revision"] = revision,
                ["changed"] = after != revision,
                ["heartbeat"] = after == revision,
                ["runs"] = RunCenter(),
                ["approvals"] = _permissions.PendingSnapshot(),
                ["queue"] = sessionId.Length == 0 ? new JArray() : _eventStore.ListQueue(sessionId),
                ["serverTime"] = ProviderStore.NowIso()
            });
        }

        private JObject RunSummary(Job job)
        {
            var runtime = job.Runtime == null ? new JObject() : job.Runtime.Snapshot;
            runtime = SecretRedactor.Sanitize(runtime) as JObject ?? new JObject();
            var status = JsonUtil.Read(job.StatusPath, new JObject()) as JObject ?? new JObject();
            var state = string.Equals(job.State, JobStates.Paused, StringComparison.Ordinal)
                ? JobStates.Paused
                : (string)status["state"] ?? job.State ?? (string)runtime["state"] ?? JobStates.Running;
            var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
            return new JObject
            {
                ["id"] = job.Id,
                ["kind"] = job.Kind,
                ["sessionId"] = job.SessionId,
                ["workspace"] = job.Workspace,
                ["providerId"] = (string)request["providerId"] ?? (string)request["provider"]?["id"] ?? "",
                ["model"] = job.Model ?? (string)request["model"] ?? "",
                ["state"] = state,
                ["isActive"] = job.IsActive,
                ["recovered"] = job.Recovered,
                ["eventCursor"] = job.TurnStartSeq,
                ["eventCount"] = job.EventCount(),
                ["startedAt"] = job.StartedAtUtc.ToString("o"),
                ["elapsedMs"] = job.ElapsedMilliseconds(),
                ["message"] = Limit(SecretRedactor.Redact((string)status["message"] ?? (string)runtime["message"] ?? ""), 600),
                ["pendingApprovals"] = _permissions.PendingCount(job.Id),
                ["jobState"] = runtime
            };
        }

        private JObject RunEvidence(string runId)
        {
            var context = _eventStore.ContextReport(runId);
            var contextBudget = context["budget"] as JObject;
            var contextSources = context["sources"] as JArray ?? new JArray();
            var artifacts = _eventStore.ListArtifacts(runId);
            var toolCalls = _eventStore.ReadToolCalls(runId);
            var tools = new JArray(toolCalls.Select(token => new JObject
            {
                ["id"] = token["id"],
                ["name"] = token["toolName"],
                ["state"] = token["state"],
                ["hasError"] = !string.IsNullOrWhiteSpace((string)token["error"]),
                ["terminalReason"] = token["terminalReason"],
                ["durationMs"] = token["durationMs"],
                ["inputPersistence"] = token["inputPersistence"],
                ["outputPersistence"] = token["outputPersistence"],
                ["startedAt"] = token["startedAt"],
                ["updatedAt"] = token["updatedAt"]
            }));
            var agentRuntimePath = Path.Combine(AppPaths.Runs, runId, "agent-runtime.json");
            var agentRuntime = JsonUtil.Read(agentRuntimePath, new JObject()) as JObject ?? new JObject();
            var childAgents = _eventStore.ListChildAgents(runId);
            return new JObject
            {
                ["runId"] = runId,
                ["context"] = context,
                ["artifacts"] = artifacts,
                ["tools"] = tools,
                ["agentRuntime"] = agentRuntime,
                ["childAgents"] = childAgents,
                ["summary"] = new JObject
                {
                    ["estimatedContextTokens"] = (long?)contextBudget?["knownInputTokens"] ?? (long?)context["estimatedTokens"] ?? 0L,
                    ["contextDecision"] = (string)contextBudget?["decision"] ?? "unavailable",
                    ["contextSources"] = contextSources.Count,
                    ["compactions"] = contextSources.Count(token => string.Equals((string)token["type"], "compaction", StringComparison.OrdinalIgnoreCase)),
                    ["artifacts"] = artifacts.Count,
                    ["tools"] = tools.Count,
                    ["failedTools"] = tools.Count(token => (bool?)token["hasError"] == true || string.Equals((string)token["state"], "failed", StringComparison.OrdinalIgnoreCase)),
                    ["timedOutTools"] = tools.Count(token => string.Equals((string)token["state"], "timed_out", StringComparison.OrdinalIgnoreCase)),
                    ["cancelledTools"] = tools.Count(token => string.Equals((string)token["state"], "cancelled", StringComparison.OrdinalIgnoreCase)),
                    ["truncatedToolValues"] = tools.Count(token => (bool?)token["inputPersistence"]?["truncated"] == true || (bool?)token["outputPersistence"]?["truncated"] == true),
                    ["redactedToolValues"] = tools.Count(token => (bool?)token["inputPersistence"]?["redacted"] == true || (bool?)token["outputPersistence"]?["redacted"] == true)
                    , ["childAgents"] = childAgents.Count
                    , ["failedChildAgents"] = childAgents.Count(token => string.Equals((string)token["state"], JobStates.Failed, StringComparison.OrdinalIgnoreCase))
                    , ["pendingHandoffs"] = childAgents.Count(token => string.Equals((string)token["state"], JobStates.Completed, StringComparison.OrdinalIgnoreCase) && !string.Equals((string)token["handoffState"], "accepted", StringComparison.OrdinalIgnoreCase))
                    , ["retryableChildAgents"] = childAgents.Count(token => (string)token["state"] == JobStates.Failed && (int?)token["attempt"] <= (int?)token["maxRetries"])
                }
            };
        }

        private JObject ResolveTextProvider(JObject payload, out string model)
        {
            if (AgentWorkerSdk.SelectedHarness(payload) == "codex")
            {
                var settings = JsonUtil.Read(AppPaths.SettingsFile, new JObject()) as JObject ?? new JObject();
                model = ((string)payload["workerModel"] ?? (string)settings["workerModel"] ?? "").Trim();
                if (model.Length == 0) model = "codex-default";
                // Codex owns authentication and model configuration; never borrow an unrelated API key.
                return new JObject { ["id"] = "codex-runtime", ["name"] = "Codex", ["text"] = new JObject
                    { ["enabled"] = true, ["protocol"] = "codex", ["models"] = new JArray(model) } };
            }
            model = ((string)payload["model"] ?? "").Trim();
            return _providers.Get((string)payload["providerId"] ?? "");
        }

        private async Task PreviewContext(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var compatibilityError = AgentWorkerSdk.CompatibilityError(payload);
            if (compatibilityError.Length > 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = compatibilityError, ["code"] = "worker_capability_unsupported" }, 400); return; }
            string model;
            var provider = ResolveTextProvider(payload, out model);
            if (provider == null || !((bool?)provider["text"]?["enabled"] ?? false))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 API 没有启用文字工作能力" }, 400); return;
            }
            if (model.Length == 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "请选择文字模型" }, 400); return; }
            var configuredModels = provider["text"]?["models"] as JArray ?? new JArray();
            if (configuredModels.Count > 0 && !configuredModels.Values<string>().Any(value => string.Equals(value, model, StringComparison.Ordinal)))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "所选模型不在当前 API 的已保存模型列表中，请重新读取模型或切换选择。", ["code"] = "model_not_configured", ["model"] = model }, 409); return;
            }
            var modelHealth = _eventStore.GetProviderHealth((string)provider["id"] ?? "", model);
            if (modelHealth != null && (bool?)modelHealth["available"] == false)
            {
                var unavailable = string.Equals((string)modelHealth["state"], "unavailable", StringComparison.Ordinal);
                var remaining = (long?)modelHealth["cooldownRemainingSeconds"] ?? 0L;
                if (remaining > 0) context.Response.Headers["Retry-After"] = remaining.ToString();
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = unavailable
                        ? "该模型已被真实请求判定为不可用。请切换模型，或在 API 设置中清除健康记录后重新验证。"
                        : "该模型因最近一次请求失败而暂时冷却，请稍后重试，或在 API 设置中清除健康记录。",
                    ["code"] = unavailable ? "model_unavailable" : "model_cooling", ["model"] = model, ["providerHealth"] = modelHealth
                }, 409); return;
            }
            var permissionMode = ((string)payload["permissionMode"] ?? "agent").Trim().ToLowerInvariant();
            if (!new[] { "readonly", "plan", "manual", "scoped", "edit", "agent", "full" }.Contains(permissionMode)) permissionMode = "readonly";
            var selectedCapability = provider?["capabilities"]?["models"]?[model] as JObject;
            var toolRequiredMode = permissionMode == "scoped" || permissionMode == "edit" || permissionMode == "agent" || permissionMode == "full";
            if (toolRequiredMode && selectedCapability?["tools"]?.Type == JTokenType.Boolean && !(bool)selectedCapability["tools"])
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "模型接口元数据明确标记为不支持 Tool calling，不能运行当前 Agent 权限模式。请选择支持 Tool 的模型，或切换到只读/规划模式。",
                    ["capability"] = "tools", ["evidence"] = selectedCapability["evidence"] ?? "provider-metadata", ["model"] = model
                }, 400); return;
            }
            string workspace;
            try { workspace = Path.GetFullPath(((string)payload["workspace"] ?? AppPaths.Workspace).Trim()); }
            catch { workspace = AppPaths.Workspace; }
            if (!Directory.Exists(workspace))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "工作区不存在：" + workspace }, 404); return;
            }
            try
            {
                var memories = _eventStore.ListWorkspaceMemories(workspace, false);
                var prepared = ContextBudgetPlanner.Prepare(payload, provider, model, workspace, memories);
                var result = (JObject)prepared.Budget.DeepClone();
                result["attachmentCount"] = prepared.Attachments.Length;
                result["matchedSkills"] = (prepared.SkillCatalog["matched"] as JArray)?.Count ?? 0;
                result["workspaceMemories"] = prepared.WorkspaceMemories.Count;
                await WriteJsonAsync(context.Response, result);
            }
            catch (ContextPreparationException error)
            {
                await WriteJsonAsync(context.Response, error.Response(), error.StatusCode);
            }
        }

        private void RestoreActiveJobs()
        {
            if (!Directory.Exists(AppPaths.Runs)) return;
            foreach (var runDir in Directory.GetDirectories(AppPaths.Runs))
            {
                try
                {
                    var id = Path.GetFileName(runDir);
                    if (_jobs.ContainsKey(id)) continue;
                    var job = Job.Chat(id, runDir, _eventStore);
                    var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
                    var status = JsonUtil.Read(job.StatusPath, new JObject { ["state"] = "running" }) as JObject ?? new JObject();
                    var state = (string)status["state"] ?? "running";
                    job.SessionId = (string)request["guiSessionId"] ?? (string)request["sessionId"] ?? "";
                    job.Workspace = (string)request["workspace"] ?? AppPaths.Workspace;
                    job.Model = (string)request["model"] ?? "";
                    job.Persistent = true;
                    int childPid;
                    var childAlive = File.Exists(job.PidPath) && int.TryParse(File.ReadAllText(job.PidPath).Trim(), out childPid) && ProcessAlive(childPid);
                    if ((state == JobStates.Completed || state == JobStates.Failed || state == JobStates.Cancelled) && !childAlive)
                    {
                        // Older builds could persist the terminal status.json first and exit before
                        // synchronising job-state.json/SQLite. Do not resurrect that Run and do not
                        // leave it looking active to diagnostics, deletion guards, or capacity logic.
                        job.Runtime.ReconcileTerminal(state, (string)status["message"] ?? "Host 启动时已对账磁盘终态",
                            new JObject { ["reconciledByHost"] = true, ["source"] = "terminal-status" });
                        _eventStore.UpdateRunState(id, state, 0, status.ToString(Formatting.None));
                        continue;
                    }
                    if (!childAlive)
                    {
                        var core = (string)request["workerHarness"] ?? "claude";
                        if (core == "dsh")
                        {
                            var interruption = "DSHarness 核心进程已退出，跨进程续接尚未验证；未自动重放可能产生副作用的输入。请检查故障现场后开始新一轮。";
                            status["state"] = JobStates.Failed; status["message"] = interruption;
                            JsonUtil.WriteAtomic(job.StatusPath, status);
                            job.Runtime.ReconcileTerminal(JobStates.Failed, interruption, new JObject { ["automaticReplaySuppressed"] = true });
                            _eventStore.UpdateRunState(id, JobStates.Failed, 0, status.ToString(Formatting.None));
                            continue;
                        }
                        request["resume"] = core == "claude" && ClaudeSessionExists(job.Workspace, (string)request["sessionId"] ?? job.SessionId);
                        var provider = request["provider"] as JObject;
                        if (provider != null && string.Equals((string)provider["protocol"], "openai", StringComparison.OrdinalIgnoreCase))
                            provider["baseUrl"] = BaseUrl.TrimEnd('/') + "/adapter/" + (string)provider["id"] + "/" + id;
                        ConfigurePermissionBroker(request, runDir, id);
                        JsonUtil.WriteAtomic(job.RequestPath, request);
                        JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = "running", ["message"] = "Host 重启后正在恢复 Native Worker", ["recoveredAt"] = ProviderStore.NowIso() });
                        job.Worker = NativeWorkerHandle.Start(request, job.OutputPath, job.ErrorPath, job.StatusPath, job.PidPath, job.InputPath, _eventStore, id, job.SessionId);
                        job.Process = job.Worker.Process;
                        state = JobStates.Running;
                    }
                    if (state == "completed" || state == "failed") job.Runtime.Transition(state == "completed" ? JobStates.Completed : JobStates.Failed, (string)status["message"] ?? "恢复后台状态");
                    else
                    {
                        var persistedState = job.Runtime.State;
                        if (!string.IsNullOrWhiteSpace(persistedState)) state = persistedState;
                    }
                    if (state == JobStates.Paused && job.Worker != null && !job.Worker.Pause())
                    {
                        state = JobStates.Running;
                        job.Runtime.Transition(JobStates.Running, "暂停态恢复失败，任务已继续运行");
                        JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = JobStates.Running, ["message"] = "暂停态恢复失败，任务已继续运行", ["recoveredAt"] = ProviderStore.NowIso() });
                    }
                    else if (state == JobStates.Paused && job.Worker != null)
                    {
                        JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = JobStates.Paused, ["message"] = "Host 重启后已恢复暂停态", ["recoveredAt"] = ProviderStore.NowIso() });
                    }
                    job.Busy = IsActiveJobState(state);
                    job.IsActive = job.Busy;
                    job.State = state;
                    job.Recovered = true;
                    _jobs[id] = job;
                }
                catch (Exception error) { CrashLog.Handled("RestoreJob", error); }
            }
        }

        private static bool ProcessAlive(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch { return false; }
        }

        private static bool IsActiveJobState(string state)
        {
            return state == JobStates.Starting || state == JobStates.Running || state == JobStates.Waiting || state == JobStates.Paused;
        }

        private static async Task PreviewFile(HttpListenerContext context)
        {
            var value = context.Request.QueryString["path"] ?? "";
            var file = Path.GetFullPath(value);
            if (!File.Exists(file))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "文件不存在或已被移动" }, 404);
                return;
            }

            var info = new FileInfo(file);
            if (info.Length > 24L * 1024 * 1024)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "图片过大，无法生成预览" }, 413);
                return;
            }

            var contentType = PreviewMimeType(file);
            if (contentType == null)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "该文件类型不支持缩略图" }, 415);
                return;
            }

            context.Response.Headers["Cache-Control"] = "no-store";
            await WriteBytesAsync(context.Response, File.ReadAllBytes(file), contentType);
        }

        private static async Task OpenLocalFile(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var file = Path.GetFullPath(((string)payload["path"] ?? "").Trim());
            if (!File.Exists(file) && !Directory.Exists(file))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "文件不存在或已被移动" }, 404);
                return;
            }

            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            await Ok(context.Response);
        }

        private async Task ExportText(HttpListenerContext context)
        {
            if (_window == null) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "窗口当前不可用" }, 503); return; }
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var saved = await _window.SaveTextAsync((string)payload["fileName"] ?? "Claude-Code-对话.txt", (string)payload["text"] ?? "");
            await WriteJsonAsync(context.Response, new JObject { ["saved"] = saved.Length > 0, ["path"] = saved });
        }

        private async Task DeleteSession(HttpListenerContext context, string id)
        {
            if (_jobs.Values.Any(job => job.IsActive && string.Equals(job.SessionId, id, StringComparison.Ordinal)))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "该会话仍有任务运行，无法永久删除", ["code"] = "session_has_background_work" }, 409);
                return;
            }
            var queued = _eventStore.ListQueue(id, false).Count;
            var scheduled = _eventStore.ListSchedules(true).OfType<JObject>().Count(item =>
                string.Equals((string)item["sessionId"], id, StringComparison.Ordinal) &&
                new[] { "scheduled", "retry", "running" }.Contains((string)item["state"] ?? ""));
            if (queued > 0 || scheduled > 0)
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "该会话仍有排队或定时任务。请先取消这些后台工作，再永久删除会话。",
                    ["code"] = "session_has_background_work", ["queued"] = queued, ["scheduled"] = scheduled
                }, 409);
                return;
            }

            foreach (var idle in _jobs.Values.Where(job => !job.IsActive && string.Equals(job.SessionId, id, StringComparison.Ordinal)).ToArray())
                RetireIdleWorker(idle);

            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var workspace = Path.GetFullPath(((string)payload["workspace"] ?? AppPaths.Workspace).Trim());
            if (!Directory.Exists(workspace)) workspace = AppPaths.Workspace;
            var transcriptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Guid.TryParse(id, out _)) transcriptIds.Add(id);
            foreach (var value in payload["transcriptIds"] as JArray ?? new JArray())
            {
                var candidate = ((string)value ?? "").Trim();
                if (Guid.TryParse(candidate, out _)) transcriptIds.Add(candidate);
            }

            var deletedTranscripts = 0;
            foreach (var transcriptId in transcriptIds)
            {
                deletedTranscripts += DeleteClaudeSessionCopies(transcriptId);
            }

            var file = Path.Combine(AppPaths.Messages, id + ".json");
            if (File.Exists(file)) File.Delete(file);
            await WriteJsonAsync(context.Response, new JObject
            {
                ["deleted"] = true, ["transcriptsDeleted"] = deletedTranscripts
            });
        }

        private async Task SaveProvider(HttpListenerContext context)
        {
            try { await WriteJsonAsync(context.Response, _providers.Upsert(JsonUtil.ObjectOrEmpty(await ReadBody(context.Request)))); }
            catch (InvalidOperationException error) { await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 400); }
        }

        private async Task DiscoverModels(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var providerId = ((string)payload["providerId"] ?? "").Trim(); var timer = Stopwatch.StartNew();
            if (providerId.Length > 0 && _providers.Get(providerId) == null)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "API 配置不存在", ["code"] = "provider_missing" }, 404); return;
            }
            try
            {
                var token = ((string)payload["token"] ?? "").Trim();
                if (token.Length == 0 && !string.IsNullOrWhiteSpace((string)payload["providerId"])) token = _providers.Token((string)payload["providerId"]);
                if (token.Length == 0) throw new InvalidOperationException("请先填写或保存 API 令牌");
                var result = await FetchModels((string)payload["baseUrl"] ?? "", token, (string)payload["authStyle"] ?? "auto");
                var images = result.Models.Where(model => ModelSupportsImage(result, model)).ToArray();
                var texts = result.Models.Where(model => ModelSupportsChat(result, model)).ToArray();
                if (providerId.Length > 0) _eventStore.RecordProviderProbe(providerId, true, "", "", timer.ElapsedMilliseconds, 0, "model-list");
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["models"] = new JArray(result.Models), ["textModels"] = new JArray(texts), ["imageModels"] = new JArray(images), ["url"] = result.Url,
                    ["capabilities"] = result.Capabilities ?? new JObject(), ["authStyle"] = result.AuthStyle
                });
            }
            catch (Exception error)
            {
                if (providerId.Length > 0) { var failure = ClassifyProviderFailure(error.Message); _eventStore.RecordProviderProbe(providerId, false, (string)failure["kind"], error.Message, timer.ElapsedMilliseconds, (int?)failure["cooldownSeconds"] ?? 0, "model-list"); }
                await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 400);
            }
        }

        private async Task ValidateProviderModel(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var providerId = ((string)payload["providerId"] ?? "").Trim();
            var model = ((string)payload["model"] ?? "").Trim();
            var provider = _providers.Get(providerId);
            if (provider == null) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "API 配置不存在", ["code"] = "provider_missing" }, 404); return; }
            var text = provider["text"] as JObject ?? new JObject();
            if (!((bool?)text["enabled"] ?? false)) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 API 没有启用文字能力", ["code"] = "text_disabled" }, 400); return; }
            var models = text["models"] as JArray ?? new JArray();
            if (model.Length == 0 || models.Count > 0 && !models.Values<string>().Any(value => string.Equals(value, model, StringComparison.Ordinal)))
            { await WriteJsonAsync(context.Response, new JObject { ["error"] = "请选择当前 API 已保存的文字模型", ["code"] = "model_not_configured" }, 400); return; }

            var protocol = ((string)text["protocol"] ?? "anthropic").Trim().ToLowerInvariant();
            var suffix = protocol == "openai" ? "/v1/chat/completions" : "/v1/messages";
            var url = OpenAiAdapter.Endpoint((string)text["baseUrl"] ?? "", suffix);
            var token = _providers.Token(providerId);
            var timer = Stopwatch.StartNew();
            try
            {
                JObject probe;
                if (protocol == "openai")
                {
                    probe = new JObject
                    {
                        ["model"] = model, ["max_tokens"] = 64, ["stream"] = false, ["temperature"] = 0,
                        ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "这是本地能力验证。请立即调用 workbench_capability_probe 工具，不要输出其他内容。" }),
                        ["tools"] = new JArray(new JObject { ["type"] = "function", ["function"] = new JObject
                        {
                            ["name"] = "workbench_capability_probe", ["description"] = "验证模型是否支持 Tool calling",
                            ["parameters"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["ok"] = new JObject { ["type"] = "boolean" } }, ["required"] = new JArray("ok") }
                        } }), ["tool_choice"] = "auto"
                    };
                }
                else
                {
                    probe = new JObject
                    {
                        ["model"] = model, ["max_tokens"] = 64,
                        ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = "这是本地能力验证。请立即调用 workbench_capability_probe 工具，不要输出其他内容。" }),
                        ["tools"] = new JArray(new JObject
                        {
                            ["name"] = "workbench_capability_probe", ["description"] = "验证模型是否支持 Tool calling",
                            ["input_schema"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["ok"] = new JObject { ["type"] = "boolean" } }, ["required"] = new JArray("ok") }
                        })
                    };
                }

                var configuredAuthStyle = ((string)provider["authStyle"] ?? "auto").Trim().ToLowerInvariant();
                var authStyles = configuredAuthStyle == "auto" ? new[] { "bearer", "x-api-key" } : new[] { configuredAuthStyle };
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
                foreach (var authStyle in authStyles)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        OpenAiAdapter.ApplyAuth(request, token, authStyle);
                        if (protocol != "openai") request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                        request.Content = new StringContent(probe.ToString(Formatting.None), Encoding.UTF8, "application/json");
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
                        {
                            var raw = await OpenAiAdapter.ReadContentLimitedAsync(response.Content, 4 * 1024 * 1024,
                                TimeSpan.FromSeconds(35), cancellation.Token, "模型验证响应正文读取超时");
                            var retryAuth = configuredAuthStyle == "auto" && authStyle != authStyles[authStyles.Length - 1] &&
                                (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden);
                            if (retryAuth) continue;
                            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("上游 HTTP " + (int)response.StatusCode + "：" + ModelValidationError(raw, token));
                            JObject data;
                            try { data = JObject.Parse(raw); } catch { throw new InvalidOperationException("上游返回的模型验证响应不是有效 JSON"); }
                            bool toolsVerified;
                            int inputTokens; int outputTokens;
                            if (protocol == "openai")
                            {
                                var message = (data["choices"] as JArray)?.OfType<JObject>().FirstOrDefault()?["message"] as JObject;
                                if (message == null) throw new InvalidOperationException("上游模型验证响应缺少 choices[0].message");
                                toolsVerified = (message["tool_calls"] as JArray)?.Count > 0;
                                inputTokens = (int?)data["usage"]?["prompt_tokens"] ?? 0; outputTokens = (int?)data["usage"]?["completion_tokens"] ?? 0;
                            }
                            else
                            {
                                if (!string.Equals((string)data["type"], "message", StringComparison.OrdinalIgnoreCase) && !(data["content"] is JArray))
                                    throw new InvalidOperationException("上游模型验证响应缺少 Anthropic message content");
                                toolsVerified = (data["content"] as JArray)?.OfType<JObject>().Any(item => string.Equals((string)item["type"], "tool_use", StringComparison.Ordinal)) == true;
                                inputTokens = (int?)data["usage"]?["input_tokens"] ?? 0; outputTokens = (int?)data["usage"]?["output_tokens"] ?? 0;
                            }
                            var health = _eventStore.RecordProviderOutcome(providerId, model, true, "", "", timer.ElapsedMilliseconds, 0, "user-model-validation");
                            if (configuredAuthStyle == "auto") _providers.RecordAuthStyle(providerId, authStyle);
                            var saved = _providers.RecordModelEvidence(providerId, model, toolsVerified, url);
                            await WriteJsonAsync(context.Response, new JObject
                            {
                                ["ok"] = true, ["providerId"] = providerId, ["model"] = model, ["latencyMs"] = timer.ElapsedMilliseconds,
                                ["chatVerified"] = true, ["toolsVerified"] = toolsVerified, ["inputTokens"] = inputTokens, ["outputTokens"] = outputTokens,
                                ["health"] = health, ["provider"] = saved, ["authStyle"] = authStyle,
                                ["evidence"] = toolsVerified ? "live-chat-tool-call" : "live-chat-response"
                            });
                            return;
                        }
                    }
                }
                throw new InvalidOperationException("没有可用的模型验证鉴权方式");
            }
            catch (Exception error)
            {
                var message = error is OperationCanceledException ? "模型验证请求超时（35 秒），连接已取消" :
                    Limit(SecretRedactor.Redact((error.Message ?? "").Replace(token, "[REDACTED]")), 1800);
                var failure = ClassifyProviderFailure(message);
                var health = _eventStore.RecordProviderOutcome(providerId, model, false, (string)failure["kind"], message, timer.ElapsedMilliseconds,
                    (int?)failure["cooldownSeconds"] ?? 0, "user-model-validation");
                await WriteJsonAsync(context.Response, new JObject { ["error"] = message, ["code"] = failure["kind"], ["classification"] = failure, ["health"] = health }, 409);
            }
        }

        private static string ModelValidationError(string raw, string token)
        {
            var value = raw ?? "";
            try
            {
                var parsed = JToken.Parse(value);
                value = (string)parsed["error"]?["message"] ?? (string)parsed["message"] ?? parsed.ToString(Formatting.None);
            }
            catch { }
            value = value.Replace(token ?? "", "[REDACTED]");
            return Limit(SecretRedactor.Redact(value), 1400);
        }

        private async Task ProbeProvider(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var providerId = ((string)payload["providerId"] ?? "").Trim(); var timer = Stopwatch.StartNew();
            if (providerId.Length > 0 && _providers.Get(providerId) == null)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "API 配置不存在", ["code"] = "provider_missing" }, 404); return;
            }
            try
            {
                var preset = ((string)payload["preset"] ?? "").Trim().ToLowerInvariant();
                var definition = ProviderPreset(preset);
                if (definition == null)
                    throw new InvalidOperationException("请选择已支持的 API 服务商；未知服务商不能仅凭 Key 安全识别，请使用“自定义 / 其他”手动填写接口");

                var token = ((string)payload["token"] ?? "").Trim();
                if (token.Length == 0 && !string.IsNullOrWhiteSpace((string)payload["providerId"]))
                    token = _providers.Token((string)payload["providerId"]);
                if (token.Length == 0) throw new InvalidOperationException("请先粘贴或保存 " + definition.Name + " API 令牌");

                ModelResult textResult;
                try { textResult = await FetchModelsFromUrls(definition.TextModelUrls, token, definition.ModelAuthStyle); }
                catch (Exception error)
                {
                    throw new InvalidOperationException(definition.Name + " 模型接口验证失败：" + error.Message);
                }

                var textModels = textResult.Models;
                var imageModels = new string[0];
                var imageSource = "";
                var warning = "";
                if (definition.SplitCombinedModels)
                {
                    imageModels = textResult.Models.Where(model => ModelSupportsImage(textResult, model)).ToArray();
                    textModels = textResult.Models.Where(model => ModelSupportsChat(textResult, model)).ToArray();
                }
                else textModels = textResult.Models.Where(model => ModelSupportsChat(textResult, model)).ToArray();

                if (definition.ImageModelUrls.Length > 0)
                {
                    try
                    {
                        var imageResult = await FetchModelsFromUrls(definition.ImageModelUrls, token, definition.ModelAuthStyle);
                        imageModels = imageResult.Models.Where(model => !IsNonChatModel(model)).ToArray();
                        imageSource = imageResult.Url;
                    }
                    catch (Exception error)
                    {
                        warning = "文字模型读取成功，但生图模型接口未通过验证：" + error.Message;
                    }
                }

                if (textModels.Length == 0)
                    throw new InvalidOperationException(definition.Name + " 已返回模型，但没有识别出可用于 Claude Code 的文字模型");

                if (providerId.Length > 0) _eventStore.RecordProviderProbe(providerId, true, "", "", timer.ElapsedMilliseconds, 0, "preset-model-list");
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["preset"] = definition.Id, ["name"] = definition.Name, ["authStyle"] = definition.AuthStyle,
                    ["text"] = new JObject
                    {
                        ["enabled"] = true, ["protocol"] = definition.TextProtocol,
                        ["baseUrl"] = definition.TextBaseUrl, ["models"] = new JArray(textModels), ["sourceUrl"] = textResult.Url
                    },
                    ["image"] = new JObject
                    {
                        ["enabled"] = imageModels.Length > 0 && definition.ImageBaseUrl.Length > 0, ["protocol"] = "openai-images",
                        ["baseUrl"] = definition.ImageBaseUrl, ["models"] = new JArray(imageModels), ["sourceUrl"] = imageSource
                    },
                    ["capabilities"] = MergeProviderCapabilities(textResult, textModels, imageModels, imageSource),
                    ["warning"] = warning
                });
            }
            catch (Exception error)
            {
                if (providerId.Length > 0) { var failure = ClassifyProviderFailure(error.Message); _eventStore.RecordProviderProbe(providerId, false, (string)failure["kind"], error.Message, timer.ElapsedMilliseconds, (int?)failure["cooldownSeconds"] ?? 0, "preset-model-list"); }
                await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 400);
            }
        }

        private static ProviderPresetDefinition ProviderPreset(string id)
        {
            if (id == "siliconflow") return new ProviderPresetDefinition
            {
                // SiliconFlow exposes an OpenAI-compatible chat/completions API.  Treating
                // /v1 as Anthropic makes Claude Code call the non-existent /v1/messages route.
                Id = id, Name = "SiliconFlow", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.siliconflow.cn/v1", ImageBaseUrl = "https://api.siliconflow.cn/v1",
                TextModelUrls = new[] { "https://api.siliconflow.cn/v1/models?type=text&sub_type=chat" },
                ImageModelUrls = new[] { "https://api.siliconflow.cn/v1/models?type=image&sub_type=text-to-image" }
            };
            if (id == "deepseek") return new ProviderPresetDefinition
            {
                Id = id, Name = "DeepSeek", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "anthropic",
                TextBaseUrl = "https://api.deepseek.com/anthropic", TextModelUrls = new[] { "https://api.deepseek.com/models" }
            };
            if (id == "moonshot") return new ProviderPresetDefinition
            {
                Id = id, Name = "Moonshot / Kimi", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.moonshot.cn/v1", TextModelUrls = new[] { "https://api.moonshot.cn/v1/models" }
            };
            if (id == "zhipu") return new ProviderPresetDefinition
            {
                Id = id, Name = "智谱 BigModel", AuthStyle = "x-api-key", ModelAuthStyle = "bearer", TextProtocol = "anthropic",
                TextBaseUrl = "https://open.bigmodel.cn/api/anthropic", ImageBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                TextModelUrls = new[] { "https://open.bigmodel.cn/api/paas/v4/models" }, SplitCombinedModels = true
            };
            if (id == "openai") return new ProviderPresetDefinition
            {
                Id = id, Name = "OpenAI", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.openai.com/v1", ImageBaseUrl = "https://api.openai.com/v1",
                TextModelUrls = new[] { "https://api.openai.com/v1/models" }, SplitCombinedModels = true
            };
            if (id == "anthropic") return new ProviderPresetDefinition
            {
                Id = id, Name = "Anthropic", AuthStyle = "x-api-key", ModelAuthStyle = "anthropic", TextProtocol = "anthropic",
                TextBaseUrl = "https://api.anthropic.com", TextModelUrls = new[] { "https://api.anthropic.com/v1/models" }
            };
            if (id == "openrouter") return new ProviderPresetDefinition
            {
                Id = id, Name = "OpenRouter", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://openrouter.ai/api/v1", TextModelUrls = new[] { "https://openrouter.ai/api/v1/models?output_modalities=text&supported_parameters=tools" }
            };
            if (id == "together") return new ProviderPresetDefinition
            {
                Id = id, Name = "Together AI", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.together.xyz/v1", ImageBaseUrl = "https://api.together.xyz/v1",
                TextModelUrls = new[] { "https://api.together.xyz/v1/models" }, SplitCombinedModels = true
            };
            if (id == "groq") return new ProviderPresetDefinition
            {
                Id = id, Name = "Groq", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.groq.com/openai/v1", TextModelUrls = new[] { "https://api.groq.com/openai/v1/models" }
            };
            if (id == "mistral") return new ProviderPresetDefinition
            {
                Id = id, Name = "Mistral AI", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.mistral.ai/v1", TextModelUrls = new[] { "https://api.mistral.ai/v1/models" }
            };
            if (id == "minimax") return new ProviderPresetDefinition
            {
                Id = id, Name = "MiniMax", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.minimaxi.com/v1", TextModelUrls = new[] { "https://api.minimaxi.com/v1/models" }
            };
            if (id == "xai") return new ProviderPresetDefinition
            {
                Id = id, Name = "xAI", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.x.ai/v1", ImageBaseUrl = "https://api.x.ai/v1",
                TextModelUrls = new[] { "https://api.x.ai/v1/language-models" },
                ImageModelUrls = new[] { "https://api.x.ai/v1/image-generation-models" }
            };
            if (id == "cerebras") return new ProviderPresetDefinition
            {
                Id = id, Name = "Cerebras", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.cerebras.ai/v1", TextModelUrls = new[] { "https://api.cerebras.ai/v1/models" }
            };
            if (id == "sambanova") return new ProviderPresetDefinition
            {
                Id = id, Name = "SambaNova", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://api.sambanova.ai/v1", TextModelUrls = new[] { "https://api.sambanova.ai/v1/models" }
            };
            if (id == "nvidia") return new ProviderPresetDefinition
            {
                Id = id, Name = "NVIDIA NIM", AuthStyle = "bearer", ModelAuthStyle = "bearer", TextProtocol = "openai",
                TextBaseUrl = "https://integrate.api.nvidia.com/v1", TextModelUrls = new[] { "https://integrate.api.nvidia.com/v1/models" }
            };
            return null;
        }

        private static bool IsImageModel(string model)
        {
            var patterns = new[] { "image", "dall-e", "dalle", "flux", "sdxl", "stable-diffusion", "seedream", "cogview", "imagen" };
            return patterns.Any(pattern => (model ?? "").IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsNonChatModel(string model)
        {
            var patterns = new[] { "embedding", "embed", "rerank", "whisper", "tts", "speech", "moderation", "guard", "audio", "video" };
            return patterns.Any(pattern => (model ?? "").IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ModelSupportsChat(ModelResult result, string model)
        {
            if (IsNonChatModel(model)) return false;
            var evidence = result?.Capabilities?[model] as JObject;
            if (string.Equals((string)evidence?["evidence"], "model-endpoint-id-only", StringComparison.Ordinal)) return !IsImageModel(model);
            return (bool?)evidence?["chat"] ?? !IsImageModel(model);
        }

        private static bool ModelSupportsImage(ModelResult result, string model)
        {
            var evidence = result?.Capabilities?[model] as JObject;
            return (bool?)evidence?["imageGeneration"] == true || IsImageModel(model);
        }

        private void NormalizeChildRequest(JObject payload)
        {
            var rawParentRunId = ((string)payload["parentRunId"] ?? "").Trim();
            if (rawParentRunId.Length == 0) return;
            var parentRunId = SafeId(rawParentRunId);
            var parentRoot = Path.Combine(AppPaths.Runs, parentRunId);
            var parentRequest = JsonUtil.Read(Path.Combine(parentRoot, "request.json"), new JObject()) as JObject ?? new JObject();
            var parentRuntime = JsonUtil.Read(Path.Combine(parentRoot, "agent-runtime.json"), new JObject()) as JObject ?? new JObject();
            var parentStatus = JsonUtil.Read(Path.Combine(parentRoot, "status.json"), new JObject()) as JObject ?? new JObject();
            if (parentRequest.Count == 0 || parentRuntime.Count == 0) throw new InvalidOperationException("Parent Run is missing its durable request or Agent Runtime boundary.");
            var parentState = (string)parentStatus["state"] ?? "running";
            if (parentState == JobStates.Failed || parentState == JobStates.Cancelled) throw new InvalidOperationException("A failed or cancelled parent Run cannot create child Agents.");
            var parentDepth = (int?)parentRuntime["depth"] ?? 0; var maxDepth = (int?)parentRuntime["maxDepth"] ?? 2;
            if (parentDepth + 1 > maxDepth) throw new InvalidOperationException("The parent Agent has reached its maximum delegation depth.");
            var sourceWorkspace = (string)parentRequest["sourceWorkspace"] ?? (string)parentRuntime["parent"]?["sourceWorkspace"] ?? "";
            if (sourceWorkspace.Length == 0 || !Directory.Exists(sourceWorkspace)) throw new InvalidOperationException("The parent source workspace is unavailable.");

            payload["parentRunId"] = parentRunId;
            payload["workspace"] = sourceWorkspace;
            payload["providerId"] = (string)parentRequest["provider"]?["id"] ?? "";
            payload["model"] = (string)parentRequest["model"] ?? "";
            payload["effort"] = (string)parentRequest["effort"] ?? "high";
            payload["workerHarness"] = (string)parentRequest["workerHarness"] ?? "auto";
            payload["workerModel"] = (string)parentRequest["workerModel"] ?? "";
            payload["maxTurns"] = Math.Max(10, Math.Min(500, (int?)parentRequest["maxTurns"] ?? 100));
            payload["permissionMode"] = (string)parentRequest["permissionMode"] ?? "readonly";
            payload["allowedTools"] = parentRequest["allowedTools"]?.DeepClone() ?? new JArray();
            payload["disallowedTools"] = parentRequest["disallowedTools"]?.DeepClone() ?? new JArray();
            payload["allowedDirs"] = new JArray();
            payload["attachments"] = new JArray();
            payload["toolRuntimePolicy"] = parentRequest["toolRuntimePolicy"]?.DeepClone() ?? ToolRuntimePolicy.DefaultManifest();
            payload["retryOf"] = ((string)payload["retryOf"] ?? "").Trim();
            payload["resume"] = false;
            if (string.IsNullOrWhiteSpace((string)payload["sessionId"])) payload["sessionId"] = Guid.NewGuid().ToString();
            if (string.IsNullOrWhiteSpace((string)payload["claudeSessionId"])) payload["claudeSessionId"] = Guid.NewGuid().ToString();
            if (string.IsNullOrWhiteSpace((string)payload["requestId"])) payload["requestId"] = "child:" + Guid.NewGuid().ToString("N");
        }

        private async Task StartChildAgent(HttpListenerContext context)
        {
            if (IsUpdateMaintenance)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "工作台正在安装更新，暂不接受新的子 Agent", ["code"] = "update_maintenance" }, 503); return;
            }
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var parentRunId = SafeId(((string)body["parentRunId"] ?? "").Trim());
            var prompt = ((string)body["prompt"] ?? "").Trim();
            if (parentRunId.Length == 0 || prompt.Length == 0)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "创建子 Agent 需要 parentRunId 和 prompt" }, 400); return;
            }
            var payload = new JObject
            {
                ["parentRunId"] = parentRunId, ["prompt"] = prompt,
                ["childName"] = Limit(((string)body["name"] ?? "子任务").Trim(), 80),
                ["sessionId"] = Guid.NewGuid().ToString(), ["claudeSessionId"] = Guid.NewGuid().ToString(),
                ["requestId"] = "child:" + Guid.NewGuid().ToString("N"), ["resume"] = false
            };
            if (body["maxRetries"] != null) payload["maxRetries"] = Math.Max(0, Math.Min(10, (int?)body["maxRetries"] ?? 2));
            if (body["dependencies"] is JArray) payload["dependencies"] = body["dependencies"].DeepClone();
            using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "api/chat/start"))
            {
                request.Headers.Add("X-Desktop-Secret", _secret); request.Headers.Add("X-Workbench-Protocol", ProtocolVersion.ToString());
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request))
                {
                    var text = await response.Content.ReadAsStringAsync(); JObject result;
                    try { result = JObject.Parse(text); } catch { result = new JObject { ["error"] = text }; }
                    context.Response.StatusCode = (int)response.StatusCode;
                    await WriteJsonAsync(context.Response, result, (int)response.StatusCode);
                }
            }
        }

        private async Task RetryChildAgent(HttpListenerContext context, string id)
        {
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request)); var parentRunId = SafeId(((string)body["parentRunId"] ?? "").Trim());
            JObject retry;
            try { retry = _eventStore.PrepareChildRetry(SafeId(id), parentRunId); }
            catch (Exception error) { await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message, ["code"] = "child_retry_blocked" }, 409); return; }
            if (retry == null) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "子 Agent 或父 Run 不匹配" }, 404); return; }
            var payload = new JObject
            {
                ["parentRunId"] = parentRunId, ["prompt"] = retry["prompt"], ["childName"] = retry["childName"],
                ["retryOf"] = retry["retryOf"], ["attempt"] = retry["attempt"], ["maxRetries"] = retry["maxRetries"],
                ["dependencies"] = retry["dependencies"]?.DeepClone() ?? new JArray(), ["sessionId"] = Guid.NewGuid().ToString(),
                ["claudeSessionId"] = Guid.NewGuid().ToString(), ["requestId"] = "child-retry:" + Guid.NewGuid().ToString("N"), ["resume"] = false
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "api/chat/start"))
            {
                request.Headers.Add("X-Desktop-Secret", _secret); request.Headers.Add("X-Workbench-Protocol", ProtocolVersion.ToString());
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request))
                {
                    var text = await response.Content.ReadAsStringAsync(); JObject result; try { result = JObject.Parse(text); } catch { result = new JObject { ["error"] = text }; }
                    await WriteJsonAsync(context.Response, result, (int)response.StatusCode);
                }
            }
        }

        private async Task ListChildAgents(HttpListenerContext context)
        {
            var parentRunId = SafeId(context.Request.QueryString["parentRunId"] ?? "");
            await WriteJsonAsync(context.Response, parentRunId.Length == 0 ? new JArray() : _eventStore.ListChildAgents(parentRunId));
        }

        private async Task AcceptChildHandoff(HttpListenerContext context, string id)
        {
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var parentRunId = SafeId(((string)body["parentRunId"] ?? "").Trim());
            JObject child;
            try { child = _eventStore.AcceptChildHandoff(SafeId(id), parentRunId); }
            catch (Exception error) { await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 409); return; }
            if (child == null) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "子 Agent 或父 Run 不匹配" }, 404); return; }
            var resultToken = child["result"] as JObject ?? new JObject();
            var resultText = (string)resultToken["result"] ?? (string)resultToken["message"] ?? resultToken.ToString(Formatting.None);
            bool ignored; resultText = ToolRuntimePolicy.RedactAndLimit(resultText, 131072, out ignored);
            var parentRequest = JsonUtil.Read(Path.Combine(AppPaths.Runs, parentRunId, "request.json"), new JObject()) as JObject ?? new JObject();
            var parentTaskId = (string)parentRequest["guiSessionId"] ?? (string)parentRequest["sessionId"] ?? "";
            _eventStore.RecordContext(parentRunId, parentTaskId, "child-agent-handoff", "子 Agent 成果交接", resultText, new JObject
            {
                ["childRunId"] = child["childRunId"], ["handoffState"] = "accepted", ["countedInPrompt"] = false
            });
            var injected = false; Job parentJob;
            if (_jobs.TryGetValue(parentRunId, out parentJob) && JobProcessAlive(parentJob))
            {
                AppendChatInput(parentJob, "已接受子 Agent " + (string)child["childRunId"] + " 的成果，请将其作为后续工作的已验证输入：\n\n" + resultText);
                injected = true;
            }
            child["injectedIntoParent"] = injected;
            await WriteJsonAsync(context.Response, child);
        }

        private async Task StartChat(HttpListenerContext context)
        {
            if (IsUpdateMaintenance)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "工作台正在安装更新，暂不接受新任务", ["code"] = "update_maintenance" }, 503); return;
            }
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            try { NormalizeChildRequest(payload); }
            catch (Exception error)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "子 Agent 继承边界无效：" + error.Message, ["code"] = "invalid_child_inheritance" }, 409); return;
            }
            var compatibilityError = AgentWorkerSdk.CompatibilityError(payload);
            if (compatibilityError.Length > 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = compatibilityError, ["code"] = "worker_capability_unsupported" }, 400); return; }
            payload["workerHarness"] = AgentWorkerSdk.SelectedHarness(payload);
            string model;
            var provider = ResolveTextProvider(payload, out model);
            if (provider == null || !((bool?)provider["text"]?["enabled"] ?? false))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 API 没有启用文字工作能力" }, 400); return;
            }
            if (model.Length == 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "请选择文字模型" }, 400); return; }
            var permissionMode = ((string)payload["permissionMode"] ?? "agent").Trim().ToLowerInvariant();
            if (!new[] { "readonly", "plan", "manual", "scoped", "edit", "agent", "full" }.Contains(permissionMode)) permissionMode = "readonly";
            var selectedCapability = provider?["capabilities"]?["models"]?[model] as JObject;
            var toolRequiredMode = permissionMode == "scoped" || permissionMode == "edit" || permissionMode == "agent" || permissionMode == "full";
            if (toolRequiredMode && selectedCapability?["tools"]?.Type == JTokenType.Boolean && !(bool)selectedCapability["tools"])
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "模型接口元数据明确标记为不支持 Tool calling，不能运行当前 Agent 权限模式。请选择支持 Tool 的模型，或切换到只读/规划模式。",
                    ["capability"] = "tools", ["evidence"] = selectedCapability["evidence"] ?? "provider-metadata", ["model"] = model
                }, 400); return;
            }

            var sessionId = ((string)payload["sessionId"] ?? "").Trim();
            if (sessionId.Length == 0) sessionId = Guid.NewGuid().ToString();
            var requestId = ((string)payload["requestId"] ?? "").Trim();
            if (requestId.Length > 0)
            {
                var idempotentJob = _jobs.Values.FirstOrDefault(value => value.Kind == "chat" && value.Runtime != null &&
                    string.Equals((string)value.Runtime.Snapshot["requestId"], requestId, StringComparison.Ordinal));
                if (idempotentJob != null)
                {
                    await WriteJsonAsync(context.Response, new JObject { ["jobId"] = idempotentJob.Id, ["existing"] = true,
                        ["idempotent"] = true, ["eventCursor"] = idempotentJob.TurnStartSeq, ["jobState"] = idempotentJob.Runtime.Snapshot });
                    return;
                }
                var durableId = _eventStore.FindRunByRequestId(requestId);
                if (durableId.Length > 0)
                {
                    var durableRoot = Path.Combine(AppPaths.Runs, durableId);
                    if (Directory.Exists(durableRoot))
                    {
                        var durableJob = Job.Chat(durableId, durableRoot, _eventStore);
                        var durableRequest = JsonUtil.Read(durableJob.RequestPath, new JObject()) as JObject ?? new JObject();
                        var durableStatus = JsonUtil.Read(durableJob.StatusPath, new JObject()) as JObject ?? new JObject();
                        durableJob.SessionId = (string)durableRequest["guiSessionId"] ?? (string)durableRequest["sessionId"] ?? "";
                        durableJob.Workspace = (string)durableRequest["workspace"] ?? AppPaths.Workspace;
                        durableJob.Model = (string)durableRequest["model"] ?? "";
                        durableJob.State = (string)durableStatus["state"] ?? JobStates.Failed;
                        durableJob.IsActive = IsActiveJobState(durableJob.State);
                        durableJob.Busy = durableJob.IsActive; durableJob.Persistent = true; durableJob.Recovered = true;
                        _jobs[durableId] = durableJob;
                        await WriteJsonAsync(context.Response, new JObject { ["jobId"] = durableId, ["existing"] = true, ["idempotent"] = true,
                            ["eventCursor"] = durableJob.TurnStartSeq, ["jobState"] = durableJob.Runtime.Snapshot });
                        return;
                    }
                }
            }
            var claudeSessionId = ((string)payload["claudeSessionId"] ?? sessionId).Trim();
            if (claudeSessionId.Length == 0) claudeSessionId = sessionId;
            string workspace;
            try { workspace = Path.GetFullPath(((string)payload["workspace"] ?? AppPaths.Workspace).Trim()); }
            catch { workspace = AppPaths.Workspace; }
            if (!Directory.Exists(workspace))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "工作区不存在：" + workspace }, 404); return;
            }
            var existingJob = _jobs.Values.FirstOrDefault(value =>
                value.IsActive && value.Kind == "chat" && string.Equals(value.SessionId, sessionId, StringComparison.Ordinal));
            if (existingJob != null)
            {
                await WriteJsonAsync(context.Response, new JObject { ["jobId"] = existingJob.Id, ["existing"] = true, ["eventCursor"] = existingJob.TurnStartSeq, ["jobState"] = existingJob.Runtime.Snapshot });
                return;
            }
            var workerRuntime = AgentWorkerSdk.Diagnostics(false, payload);
            if (!((bool?)workerRuntime["available"] ?? false))
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "未找到可用的 Agent Worker。请在“设置 → 工作区”中选择 Claude Code、Codex 或自定义 Worker。",
                    ["code"] = "agent_worker_unavailable", ["agentWorkers"] = workerRuntime
                }, 503); return;
            }
            var extensionTrust = ExtensionTrustPolicy.WorkspaceSummary(workspace);
            if ((bool?)extensionTrust["blocked"] ?? false)
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "工作区包含尚未授权或授权后已修改的项目级 Skill/Agent。为防止仓库提示词在未确认时进入 Claude Code，请先在“扩展”中检查并信任或删除这些项目扩展。",
                    ["code"] = "untrusted_project_extensions",
                    ["extensionTrust"] = extensionTrust
                }, 409); return;
            }
            if (_jobs.Values.Count(value => value.IsActive && value.Kind == "chat") >= MaxConcurrentWorkers)
            {
                context.Response.Headers["Retry-After"] = "2";
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "并行 Agent 已达到上限，任务可进入持久队列等待", ["code"] = "worker_capacity", ["maxConcurrentWorkers"] = MaxConcurrentWorkers }, 429); return;
            }

            var sourceWorkspace = workspace;
            PreparedContext prepared;
            try { prepared = ContextBudgetPlanner.Prepare(payload, provider, model, sourceWorkspace, _eventStore.ListWorkspaceMemories(sourceWorkspace, false)); }
            catch (ContextPreparationException error)
            {
                await WriteJsonAsync(context.Response, error.Response(), error.StatusCode); return;
            }
            if (string.Equals((string)prepared.Budget["decision"], "blocked", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "本轮 Context 已超过当前模型的可用输入预算，请缩短要求、减少引用或切换更大窗口模型",
                    ["contextBudget"] = prepared.Budget
                }, 413); return;
            }
            var attachments = prepared.Attachments;
            var prompt = prepared.Prompt;
            var skillCatalog = prepared.SkillCatalog;
            var trustedRuntime = ExtensionTrustPolicy.TrustedRuntimeInputs(sourceWorkspace);
            try
            {
                McpRuntimePolicy.ValidateTrustedConfigs(trustedRuntime["mcpConfigs"] as JArray ?? new JArray(), sourceWorkspace);
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "项目 MCP 配置未通过运行前校验：" + error.Message,
                    ["code"] = "invalid_mcp_runtime_config"
                }, 400); return;
            }

            string[] allowedDirs;
            try
            {
                allowedDirs = (payload["allowedDirs"] as JArray ?? new JArray())
                    .Select(value => ((string)value ?? "").Trim()).Where(value => value.Length > 0)
                    .Select(Path.GetFullPath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "会话文件授权无效：" + error.Message }, 400); return;
            }
            var agentRoots = permissionMode == "agent" || permissionMode == "full" ? AgentRoots() : new string[0];
            var addDirs = attachments.Select(Path.GetDirectoryName).Concat(allowedDirs).Concat(agentRoots)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var allowedTools = permissionMode == "scoped" ? payload["allowedTools"] as JArray ?? new JArray() : new JArray();
            var toolRuntimePolicy = ToolRuntimeSettings.From(payload["toolRuntimePolicy"] as JObject).Manifest();
            var reuseFingerprint = WorkerReuseFingerprint(payload, provider, model, sourceWorkspace, permissionMode, addDirs,
                allowedTools, toolRuntimePolicy, trustedRuntime, claudeSessionId, prepared.MemoryText);
            var idleSessionWorkers = _jobs.Values.Where(value => value.Persistent && value.Kind == "chat" && !value.IsActive &&
                string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) && JobProcessAlive(value))
                .OrderByDescending(value => value.StartedAtUtc).ToArray();
            var reusableJob = idleSessionWorkers.FirstOrDefault(value =>
            {
                var previous = JsonUtil.Read(value.RequestPath, new JObject()) as JObject ?? new JObject();
                return string.Equals((string)previous["reuseFingerprint"] ?? "", reuseFingerprint, StringComparison.Ordinal);
            });
            foreach (var stale in idleSessionWorkers.Where(value => value != reusableJob)) RetireIdleWorker(stale);
            if (reusableJob != null)
            {
                _eventStore.RecordContext(reusableJob.Id, sessionId, "context-budget", "发送前 Context 预算决策", "", (JObject)prepared.Budget.DeepClone());
                _eventStore.RecordContext(reusableJob.Id, sessionId, "user", "用户要求", (string)payload["prompt"] ?? "", new JObject());
                _eventStore.RecordContext(reusableJob.Id, sessionId, "provider-route", "Provider 与模型路由", "", new JObject
                {
                    ["providerId"] = provider["id"], ["model"] = model, ["protocol"] = provider["text"]?["protocol"], ["countedInPrompt"] = false
                });
                if (prepared.SkillHint.Length > 0) _eventStore.RecordContext(reusableJob.Id, sessionId, "skill-metadata", "按需命中的 Skill", prepared.SkillHint, new JObject { ["fullBodyPreloaded"] = false });
                RecordWorkspaceMemoryContext(reusableJob.Id, sessionId, prepared);
                foreach (var attachment in attachments)
                {
                    _eventStore.RecordContext(reusableJob.Id, sessionId, "attachment-reference", Path.GetFileName(attachment), attachment, new JObject { ["path"] = attachment, ["contentEmbedded"] = false });
                    _eventStore.RecordArtifact(sessionId, reusableJob.Id, "attachment", attachment, GuessMimeType(attachment), new JObject { ["source"] = "path-reference" });
                }
                var reusedOffset = AppendChatInput(reusableJob, prepared.Prompt);
                await WriteJsonAsync(context.Response, new JObject { ["jobId"] = reusableJob.Id, ["reused"] = true, ["inputOffset"] = reusedOffset, ["eventCursor"] = reusableJob.TurnStartSeq, ["jobState"] = reusableJob.Runtime.Snapshot, ["contextBudget"] = prepared.Budget });
                return;
            }

            var id = Guid.NewGuid().ToString();
            var runDir = Path.Combine(AppPaths.Runs, id);
            Directory.CreateDirectory(runDir);
            var job = Job.Chat(id, runDir, _eventStore);
            job.SessionId = sessionId;
            job.Workspace = workspace;
            job.Model = model;
            job.Persistent = true;
            job.Busy = true;
            job.Runtime.SetIdentity(sessionId, workspace, model);
            job.Runtime.SetRequestId(requestId);
            job.Runtime.Transition(JobStates.Starting, "正在启动 Agent Worker");
            _eventStore.UpsertTask(sessionId, workspace, Limit((string)payload["prompt"] ?? "新的 Agent 任务", 80), JobStates.Starting, false, "{}");
            _eventStore.UpsertRun(id, sessionId, requestId, model, workspace, JobStates.Starting, 0);
            JObject request;
            long inputOffset;
            try
            {
                InjectRunPreparationFailure("after-run-created");
                File.WriteAllText(job.OutputPath, "", new UTF8Encoding(false));
                File.WriteAllText(job.ErrorPath, "", new UTF8Encoding(false));

                var textConfig = provider["text"] as JObject ?? new JObject();
                var baseUrl = (string)textConfig["baseUrl"] ?? "";
                if ((string)textConfig["protocol"] == "openai") baseUrl = BaseUrl.TrimEnd('/') + "/adapter/" + (string)provider["id"] + "/" + id;
                JsonUtil.WriteAtomic(Path.Combine(runDir, "skill-index.json"), skillCatalog);
                var trustedInstructionText = prepared.SystemInstructionText;
                var trustedInstructionPath = Path.Combine(runDir, "trusted-project-instructions.md");
                if (trustedInstructionText.Length > 0) File.WriteAllText(trustedInstructionPath, trustedInstructionText, new UTF8Encoding(false));
                var isolated = TaskWorkspaceManager.Prepare(sourceWorkspace, id, runDir,
                    permissionMode == "edit" || permissionMode == "agent" || permissionMode == "full", sessionId);
                workspace = isolated.WorkerWorkspace;
                job.Workspace = workspace;
                job.Runtime.SetIdentity(sessionId, workspace, model);
                _eventStore.UpsertRun(id, sessionId, requestId, model, workspace, JobStates.Starting, 0);
                request = new JObject
                {
                ["workspace"] = workspace, ["sourceWorkspace"] = sourceWorkspace, ["prompt"] = prompt,
                ["guiSessionId"] = sessionId,
                ["sessionId"] = claudeSessionId,
                ["resume"] = (string)payload["workerHarness"] == "claude" && ((bool?)payload["resume"] ?? false) && EnsureClaudeSessionForWorker(sourceWorkspace, workspace, claudeSessionId),
                ["workerHarness"] = (string)payload["workerHarness"] ?? (string)(JsonUtil.Read(AppPaths.SettingsFile, new JObject()) as JObject)?["workerHarness"] ?? "auto",
                ["workerModel"] = (string)payload["workerModel"] ?? (string)(JsonUtil.Read(AppPaths.SettingsFile, new JObject()) as JObject)?["workerModel"] ?? "",
                ["model"] = model, ["effort"] = (string)payload["effort"] ?? "high", ["permissionMode"] = permissionMode,
                ["maxTurns"] = Math.Max(10, Math.Min(500, (int?)payload["maxTurns"] ?? 100)),
                ["attachments"] = new JArray(attachments), ["addDirs"] = new JArray(addDirs),
                ["skillIndexPath"] = Path.Combine(runDir, "skill-index.json"), ["matchedSkills"] = skillCatalog["matched"] == null ? new JArray() : skillCatalog["matched"].DeepClone(),
                ["trustedInstructionPath"] = trustedInstructionText.Length > 0 ? trustedInstructionPath : "",
                ["trustedInstructionSources"] = prepared.TrustedInstructionSources.DeepClone(),
                ["workspaceMemories"] = new JArray(prepared.WorkspaceMemories.OfType<JObject>().Select(memory => new JObject
                {
                    ["id"] = memory["id"], ["title"] = memory["title"], ["estimatedTokens"] = memory["estimatedTokens"]
                })),
                ["trustedMcpConfigs"] = trustedRuntime["mcpConfigs"]?.DeepClone() ?? new JArray(),
                ["trustedAgents"] = trustedRuntime["agents"]?.DeepClone() ?? new JObject(),
                ["allowedTools"] = allowedTools,
                ["disallowedTools"] = payload["disallowedTools"] as JArray ?? new JArray(),
                ["reuseFingerprint"] = reuseFingerprint,
                ["provider"] = new JObject
                {
                    ["id"] = provider["id"], ["protocol"] = textConfig["protocol"], ["sourceBaseUrl"] = textConfig["baseUrl"],
                    ["baseUrl"] = baseUrl, ["authStyle"] = provider["authStyle"] ?? "auto", ["tokenEncrypted"] = provider["tokenEncrypted"]
                }
                };
                request["parentRunId"] = ((string)payload["parentRunId"] ?? "").Trim();
            request["childName"] = ((string)payload["childName"] ?? "").Trim();
            request["attempt"] = Math.Max(1, (int?)payload["attempt"] ?? 1);
            request["maxRetries"] = Math.Max(0, Math.Min(10, (int?)payload["maxRetries"] ?? 2));
            request["retryOf"] = ((string)payload["retryOf"] ?? "").Trim();
            request["dependencies"] = payload["dependencies"] as JArray ?? new JArray();
            request["toolRuntimePolicy"] = toolRuntimePolicy.DeepClone();
            prepared.Budget = ContextBudgetPlanner.Plan(provider, model, prepared.UserPrompt, prepared.AttachmentHint, prepared.SkillHint,
                (bool?)request["resume"] ?? false, prepared.History, prepared.TrustedInstructionText, prepared.MemoryText);
            var policy = TaskSecurityPolicy.Create(id, permissionMode, workspace, addDirs, allowedTools, request["disallowedTools"] as JArray);
            JsonUtil.WriteAtomic(Path.Combine(runDir, "security-policy.json"), policy);
            JsonUtil.WriteAtomic(Path.Combine(runDir, "tool-runtime-policy.json"), toolRuntimePolicy);
            _eventStore.RecordContext(id, sessionId, "context-budget", "发送前 Context 预算决策", "", (JObject)prepared.Budget.DeepClone());
            _eventStore.RecordContext(id, sessionId, "user", "用户要求", (string)payload["prompt"] ?? "", new JObject());
            _eventStore.RecordContext(id, sessionId, "permission", "任务权限清单", policy.ToString(Formatting.None), new JObject { ["mode"] = permissionMode, ["countedInPrompt"] = false });
            _eventStore.RecordContext(id, sessionId, "tool-runtime-policy", "Tool Runtime 策略", "", (JObject)toolRuntimePolicy.DeepClone());
            _eventStore.RecordContext(id, sessionId, "provider-route", "Provider 与模型路由", "", new JObject
            {
                ["providerId"] = provider["id"], ["model"] = model, ["protocol"] = textConfig["protocol"], ["countedInPrompt"] = false
            });
            if ((bool?)request["resume"] == true) _eventStore.RecordContext(id, sessionId, "conversation-history", "Claude session 历史", "", new JObject
            {
                ["sessionId"] = claudeSessionId,
                ["storedExternally"] = true,
                ["estimatedTokens"] = Math.Max(0L, (long?)prepared.Budget["historyInputTokens"] ?? 0L),
                ["tokenEstimateAvailable"] = (bool?)prepared.Budget["historyMeasured"] ?? false,
                ["historyEvidence"] = (string)prepared.Budget["historyEvidence"] ?? "unavailable",
                ["countedInPrompt"] = true
            });
            var routingHint = prepared.SkillHint;
            if (routingHint.Length > 0) _eventStore.RecordContext(id, sessionId, "skill-metadata", "按需命中的 Skill", routingHint, new JObject { ["fullBodyPreloaded"] = false });
            RecordWorkspaceMemoryContext(id, sessionId, prepared);
            foreach (var attachment in attachments)
            {
                _eventStore.RecordContext(id, sessionId, "attachment-reference", Path.GetFileName(attachment), attachment, new JObject { ["path"] = attachment, ["contentEmbedded"] = false });
                _eventStore.RecordArtifact(sessionId, id, "attachment", attachment, GuessMimeType(attachment), new JObject { ["source"] = "path-reference" });
            }
            ConfigurePermissionBroker(request, runDir, id);
            JObject mcpRuntime;
            try
            {
                mcpRuntime = McpRuntimePolicy.BuildRuntimeManifest(request["mcpConfigs"] as JArray ?? new JArray(), sourceWorkspace, runDir);
            }
            catch (Exception error)
            {
                FailPreparedChat(job, error.Message, "mcp-runtime");
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "MCP 运行时清单生成失败：" + error.Message, ["code"] = "invalid_mcp_runtime_config" }, 400); return;
            }
            request["mcpRuntime"] = mcpRuntime.DeepClone();
            JsonUtil.WriteAtomic(Path.Combine(runDir, "mcp-runtime.json"), mcpRuntime);
            _eventStore.RecordContext(id, sessionId, "mcp-runtime", "显式 MCP 运行时清单", "", (JObject)mcpRuntime.DeepClone());
            var parentRunId = (string)request["parentRunId"] ?? "";
            var parentRuntime = parentRunId.Length == 0 ? new JObject() : JsonUtil.Read(Path.Combine(AppPaths.Runs, parentRunId, "agent-runtime.json"), new JObject()) as JObject ?? new JObject();
            var agentDepth = parentRunId.Length == 0 ? 0 : (int?)parentRuntime["depth"] + 1 ?? 1;
            var agentRuntime = AgentRuntimePolicy.Build(id, sessionId, parentRunId, agentDepth, sourceWorkspace, workspace,
                permissionMode, policy, toolRuntimePolicy, request["trustedAgents"] as JObject, mcpRuntime, allowedTools, request["disallowedTools"] as JArray);
            try { AgentRuntimePolicy.Validate(agentRuntime, sourceWorkspace, workspace); }
            catch (Exception error)
            {
                FailPreparedChat(job, error.Message, "agent-runtime");
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "Agent Runtime 边界无效：" + error.Message, ["code"] = "invalid_agent_runtime" }, 400); return;
            }
            if ((string)request["workerHarness"] == "codex" || (string)request["workerHarness"] == "dsh")
            {
                agentRuntime["mode"] = "selected-cli-native-policy";
                agentRuntime["workbenchAgentDefinitionsApplied"] = false;
                agentRuntime["agents"] = new JObject();
            }
            request["agentRuntime"] = agentRuntime.DeepClone();
            request["trustedAgents"] = agentRuntime["agents"]?.DeepClone() ?? new JObject();
            if (parentRunId.Length > 0)
            {
                try
                {
                    _eventStore.CreateChildAgent(parentRunId, id, sessionId, agentDepth, new JObject
                    {
                        ["name"] = (string)request["childName"] ?? "子任务", ["workspace"] = workspace, ["permissionMode"] = permissionMode,
                        ["promptSha256"] = Sha256Text(prompt), ["inheritance"] = "restrict_only", ["attempt"] = request["attempt"], ["maxRetries"] = request["maxRetries"], ["dependencies"] = request["dependencies"]
                        , ["retryOf"] = request["retryOf"]
                    });
                }
                catch (Exception error)
                {
                    FailPreparedChat(job, error.Message, "child-agent");
                    await WriteJsonAsync(context.Response, new JObject { ["error"] = "子 Agent 创建失败：" + error.Message, ["code"] = "child_agent_limit" }, 409); return;
                }
            }
            JsonUtil.WriteAtomic(Path.Combine(runDir, "agent-runtime.json"), agentRuntime);
            _eventStore.RecordContext(id, sessionId, "agent-runtime", "父子 Agent 边界", "", (JObject)agentRuntime.DeepClone());
            if ((string)request["workerHarness"] == "claude") _eventStore.RecordContext(id, sessionId, "claude-code-isolation", "Claude Code 原生加载边界", "", (JObject)((request["claudeCodeIsolation"] ?? new JObject
            {
                ["mode"] = "bare", ["implicitProjectSettings"] = false, ["implicitHooks"] = false,
                ["implicitPlugins"] = false, ["implicitMcp"] = false
            }).DeepClone()));
            else _eventStore.RecordContext(id, sessionId, "core-isolation", "命令行核心自身权限边界", "", request["coreIsolation"] as JObject ?? new JObject());
            JsonUtil.WriteAtomic(job.RequestPath, request);
                File.WriteAllText(job.InputPath, "", new UTF8Encoding(false));
                inputOffset = AppendChatInput(job, prompt);
            }
            catch (Exception error)
            {
                FailPreparedChat(job, error.Message, "run-preparation");
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["error"] = "Agent 启动准备失败：" + SecretRedactor.Redact(error.Message),
                    ["code"] = "agent_preparation_failed"
                }, 500);
                return;
            }

            _jobs[id] = job;
            try
            {
                job.StartedAtUtc = DateTimeOffset.UtcNow;
                NativeMetrics.RecordRunStart(id);
                job.Worker = NativeWorkerHandle.Start(request, job.OutputPath, job.ErrorPath, job.StatusPath, job.PidPath, job.InputPath, _eventStore, id, sessionId);
                job.Process = job.Worker.Process;
                _eventStore.UpsertRun(id, sessionId, requestId, model, workspace, JobStates.Running, job.Process.Id);
            }
            catch (Exception error)
            {
                job.IsActive = false; job.Busy = false; job.State = "failed";
                NativeMetrics.RecordWorkerError("WorkerStart: " + error.Message);
                job.Runtime.Transition(JobStates.Failed, error.Message);
                JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = "failed", ["message"] = error.Message });
                _eventStore.UpdateRunState(id, JobStates.Failed, 0, new JObject { ["error"] = error.Message }.ToString(Formatting.None));
                NativeMetrics.RecordRunEnd(id, JobStates.Failed, job.ElapsedMilliseconds());
                NotifyState();
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "Agent Worker 启动失败：" + error.Message }, 500); return;
            }
            NotifyState();
            await WriteJsonAsync(context.Response, new JObject { ["jobId"] = id, ["inputOffset"] = inputOffset, ["eventCursor"] = job.TurnStartSeq, ["jobState"] = job.Runtime.Snapshot, ["contextBudget"] = prepared.Budget });
        }

        private static void InjectRunPreparationFailure(string stage)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_MODE"), "1", StringComparison.Ordinal)) return;
            if (!string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_FAIL_RUN_PREPARATION"), stage, StringComparison.Ordinal)) return;
            throw new IOException("Injected run preparation failure: " + stage);
        }

        private void FailPreparedChat(Job job, string message, string stage)
        {
            if (job == null) return;
            message = string.IsNullOrWhiteSpace(message) ? "Agent 启动前校验失败" : SecretRedactor.Redact(message);
            var status = new JObject
            {
                ["state"] = JobStates.Failed,
                ["message"] = message,
                ["worker"] = "native",
                ["preflightStage"] = stage ?? "preflight",
                ["finishedAt"] = ProviderStore.NowIso()
            };
            job.IsActive = false;
            job.Busy = false;
            job.State = JobStates.Failed;
            job.Error = message;
            try { job.Runtime.Transition(JobStates.Failed, message, new JObject { ["preflightStage"] = stage ?? "preflight" }); }
            catch (Exception error) { CrashLog.Handled("PreparedChatRuntime", error); }
            try { JsonUtil.WriteAtomic(job.StatusPath, status); }
            catch (Exception error) { CrashLog.Handled("PreparedChatStatus", error); }
            try { File.WriteAllText(job.ErrorPath, message, new UTF8Encoding(false)); } catch { }
            try { _eventStore.UpdateRunState(job.Id, JobStates.Failed, 0, status.ToString(Formatting.None)); }
            catch (Exception error) { CrashLog.Handled("PreparedChatEventStore", error); }
        }

        private async Task CheckSchedulesAsync()
        {
            if (!IsRunning || IsUpdateMaintenance || Interlocked.Exchange(ref _schedulerChecking, 1) != 0) return;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var owner = "host:" + Process.GetCurrentProcess().Id;
                await ReconcileRunningSchedulesAsync(owner, now);
                foreach (var token in _eventStore.ClaimDueSchedules(owner, now, 20).OfType<JObject>())
                {
                    var scheduleId = (string)token["id"] ?? "";
                    var sessionId = ((string)token["sessionId"] ?? "").Trim();
                    var active = _jobs.Values.Any(job => job.IsActive && job.Kind == "chat" && string.Equals(job.SessionId, sessionId, StringComparison.Ordinal));
                    var conflict = ((string)token["conflictPolicy"] ?? "queue").ToLowerInvariant();
                    if (active)
                    {
                        if (conflict == "skip") _eventStore.CompleteSchedule(scheduleId, true, "skipped-active", now);
                        else _eventStore.DeferSchedule(scheduleId, now.AddSeconds(30), "waiting-active");
                        continue;
                    }
                    var settings = JsonUtil.Read(AppPaths.SettingsFile, new JObject()) as JObject ?? new JObject();
                    var workspace = (string)token["workspace"] ?? (string)settings["workspace"] ?? AppPaths.Workspace;
                    var session = (JsonUtil.Read(AppPaths.SessionsFile, new JArray()) as JArray ?? new JArray()).OfType<JObject>().FirstOrDefault(value => string.Equals((string)value["id"], sessionId, StringComparison.Ordinal));
                    var claudeSessionId = (string)session?["claudeSessionId"] ?? sessionId;
                    var payload = new JObject
                    {
                        ["workspace"] = workspace, ["prompt"] = (string)token["text"] ?? "", ["attachments"] = new JArray(), ["allowedDirs"] = session?["allowedDirs"] as JArray ?? new JArray(),
                        ["sessionId"] = sessionId, ["claudeSessionId"] = claudeSessionId, ["resume"] = (bool?)session?["started"] ?? false,
                        ["providerId"] = (string)token["providerId"] ?? (string)settings["providerId"] ?? "",
                        ["model"] = (string)token["model"] ?? (string)settings["model"] ?? "",
                        ["effort"] = (string)token["effort"] ?? (string)settings["effort"] ?? "high",
                        ["workerHarness"] = (string)token["workerHarness"] ?? (string)settings["workerHarness"] ?? "auto",
                        ["workerModel"] = (string)token["workerModel"] ?? (string)settings["workerModel"] ?? "",
                        ["maxTurns"] = Math.Max(10, Math.Min(500, (int?)token["maxTurns"] ?? (int?)settings["maxTurns"] ?? 100)),
                        ["permissionMode"] = (string)token["permissionMode"] ?? (string)settings["permissionMode"] ?? "agent",
                        ["allowedTools"] = ToolArray(token["allowedTools"] ?? settings["allowedTools"]),
                        ["disallowedTools"] = ToolArray(token["disallowedTools"] ?? settings["disallowedTools"]),
                        ["requestId"] = "schedule:" + scheduleId + ":" + ((string)token["at"] ?? now.ToString("o"))
                    };
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "api/chat/start"))
                        {
                            request.Headers.Add("X-Desktop-Secret", _secret);
                            request.Headers.Add("X-Workbench-Protocol", ProtocolVersion.ToString());
                            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                            using (var result = await Http.SendAsync(request))
                            {
                                var responseText = await result.Content.ReadAsStringAsync();
                                if (!result.IsSuccessStatusCode) throw new InvalidOperationException(Limit(responseText, 1000));
                                JObject started; try { started = JObject.Parse(responseText); } catch { started = new JObject(); }
                                var runId = ((string)started["jobId"] ?? "").Trim();
                                if (runId.Length == 0) throw new InvalidOperationException("后台调度未返回可跟踪的 Run ID");
                                _eventStore.BindScheduleRun(scheduleId, runId, owner, now);
                            }
                        }
                        if (session != null) { session["started"] = true; session["updatedAt"] = ProviderStore.NowIso(); JsonUtil.WriteAtomic(AppPaths.SessionsFile, session.Parent); }
                        if (_window != null) _window.ShowNotification("Claude Code 后台调度", "定时任务已启动，后端会等待 Agent 的真实终态");
                    }
                    catch (Exception error) { _eventStore.CompleteSchedule(scheduleId, false, error.Message, now); CrashLog.Handled("SchedulerRun", error); }
                }
            }
            catch (Exception error) { CrashLog.Handled("Scheduler", error); }
            finally { Interlocked.Exchange(ref _schedulerChecking, 0); }
        }

        private async Task ReconcileRunningSchedulesAsync(string owner, DateTimeOffset now)
        {
            foreach (var schedule in _eventStore.ListRunningSchedules().OfType<JObject>())
            {
                var scheduleId = (string)schedule["id"] ?? ""; var runId = ((string)schedule["activeRunId"] ?? "").Trim();
                if (runId.Length == 0)
                {
                    _eventStore.CompleteSchedule(scheduleId, false, "调度记录缺少 active Run ID", now);
                    continue;
                }
                var snapshot = _eventStore.GetRunSnapshot(runId);
                var state = ((string)snapshot?["state"] ?? "").Trim().ToLowerInvariant();
                if (state == JobStates.Completed || state == JobStates.Failed || state == JobStates.Cancelled)
                {
                    var details = snapshot?["details"] as JObject ?? new JObject();
                    var error = (string)details["error"] ?? (string)details["message"] ?? (state == JobStates.Completed ? "" : "Agent Run " + state);
                    _eventStore.CompleteSchedule(scheduleId, state == JobStates.Completed, error, now);
                    if (_window != null) _window.ShowNotification(state == JobStates.Completed ? "Claude Code 后台调度完成" : "Claude Code 后台调度失败", state == JobStates.Completed ? "定时任务已完成" : Limit(error, 220));
                }
                else _eventStore.RenewScheduleLease(scheduleId, owner, now);
            }
            await Task.CompletedTask;
        }

        private static JArray ToolArray(JToken token)
        {
            var array = token as JArray; if (array != null) return array;
            return new JArray(((string)token ?? "").Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0));
        }

        private async Task ListTaskQueue(HttpListenerContext context)
        {
            var taskId = (context.Request.QueryString["sessionId"] ?? "").Trim();
            if (taskId.Length == 0) { await WriteJsonAsync(context.Response, new JArray()); return; }
            await WriteJsonAsync(context.Response, _eventStore.ListQueue(taskId, context.Request.QueryString["history"] == "1"));
        }

        private async Task EnqueueTask(HttpListenerContext context)
        {
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var taskId = ((string)body["sessionId"] ?? "").Trim(); var text = ((string)body["text"] ?? "").Trim();
            if (taskId.Length == 0 || text.Length == 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "队列任务缺少会话或指令" }, 400); return; }
            var payload = body["request"] as JObject ?? new JObject();
            payload["prompt"] = text; payload["sessionId"] = taskId;
            if (string.IsNullOrWhiteSpace((string)payload["claudeSessionId"])) payload["claudeSessionId"] = taskId;
            if (payload["resume"] == null || payload["resume"].Type == JTokenType.Null) payload["resume"] = true;
            var item = _eventStore.Enqueue(taskId, text, ((string)body["kind"] ?? "queued").Trim(), payload);
            TouchActivity();
            await WriteJsonAsync(context.Response, item);
        }

        private async Task PrioritizeTaskQueue(HttpListenerContext context, string id)
        {
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var ok = _eventStore.PrioritizeQueue(SafeId(id), (bool?)body["steer"] ?? true);
            if (ok) TouchActivity();
            await WriteJsonAsync(context.Response, new JObject { ["ok"] = ok });
        }

        private async Task CheckTaskQueueAsync()
        {
            if (!IsRunning || IsUpdateMaintenance || Interlocked.Exchange(ref _queueChecking, 1) != 0) return;
            var changed = false;
            try
            {
                InjectBackgroundLoopFailure("queue", ref TestQueueLoopFailureInjected);
                foreach (var token in _eventStore.QueueInProgress().OfType<JObject>())
                {
                    var runId = (string)token["runId"] ?? ""; Job running;
                    if (runId.Length == 0 || !_jobs.TryGetValue(runId, out running)) continue;
                    var status = JsonUtil.Read(running.StatusPath, new JObject()) as JObject ?? new JObject();
                    var state = (string)status["state"] ?? "running";
                    var expectedOffset = (long?)token["inputOffset"] ?? 0L; var completedOffset = 0L;
                    var inputState = JsonUtil.Read(running.InputStatePath, new JObject()) as JObject ?? new JObject();
                    completedOffset = (long?)inputState["completedOffset"] ?? 0L;
                    var sentOffset = (long?)inputState["sentOffset"] ?? 0L;
                    if (expectedOffset > 0 && completedOffset >= expectedOffset)
                    {
                        changed |= _eventStore.TransitionQueueAny((string)token["id"], state == "failed" ? "failed" : "completed", runId, expectedOffset);
                        if (sentOffset > 0 && completedOffset >= sentOffset)
                            TryFinalizeTerminalJob(running, status, state, true);
                        continue;
                    }
                    if (state != "failed" && state != "cancelled") continue;
                    changed |= _eventStore.TransitionQueueAny((string)token["id"], state, runId, expectedOffset);
                }
                foreach (var token in _eventStore.QueuedItems().OfType<JObject>())
                {
                    var queueId = (string)token["id"]; var taskId = (string)token["taskId"];
                    var active = _jobs.Values.FirstOrDefault(job => job.Kind == "chat" && job.IsActive && string.Equals(job.SessionId, taskId, StringComparison.Ordinal));
                    if (active != null)
                    {
                        if ((string)token["kind"] != "steer") continue;
                        if (!SupportsLiveSteering(active)) continue;
                        if (!_eventStore.TransitionQueue(queueId, "queued", "starting", active.Id)) continue;
                        changed = true;
                        var steerOffset = AppendChatInput(active, "方向调整：下面是当前最高优先级的新要求。请立即据此调整后续工作方向；已经完成且仍适用的结果可以保留。\n\n" + (string)token["text"]);
                        changed |= _eventStore.TransitionQueueAny(queueId, "running", active.Id, steerOffset);
                        continue;
                    }
                    if (_jobs.Values.Count(job => job.Kind == "chat" && job.IsActive) >= MaxConcurrentWorkers) continue;
                    if (!_eventStore.TransitionQueue(queueId, "queued", "starting")) continue;
                    changed = true;
                    var payload = token["payload"] as JObject ?? new JObject();
                    payload["requestId"] = (string)token["requestId"];
                    payload["prompt"] = (string)token["text"];
                    payload["sessionId"] = taskId; payload["resume"] = true;
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "api/chat/start"))
                        {
                            request.Headers.Add("X-Desktop-Secret", _secret); request.Headers.Add("X-Workbench-Protocol", ProtocolVersion.ToString());
                            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                            using (var response = await Http.SendAsync(request))
                            {
                                var text = await response.Content.ReadAsStringAsync(); var result = JObject.Parse(text);
                                if (!response.IsSuccessStatusCode) throw new InvalidOperationException((string)result["error"] ?? "队列任务启动失败");
                                changed |= _eventStore.TransitionQueueAny(queueId, "running", (string)result["jobId"] ?? "", (long?)result["inputOffset"] ?? 0L);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        CrashLog.Handled("TaskQueue", error); changed |= _eventStore.TransitionQueueAny(queueId, "failed");
                    }
                }
            }
            catch (Exception error) { CrashLog.Handled("TaskQueueLoop", error); }
            finally { if (changed) TouchActivity(); Interlocked.Exchange(ref _queueChecking, 0); }
        }

        private static void AdvanceSchedule(JObject schedule, DateTime now, string state)
        {
            schedule["lastRunAt"] = now.ToString("o"); schedule["lastState"] = state; schedule.Remove("lastError");
            var repeat = (int?)schedule["repeatMinutes"] ?? 0;
            if (repeat > 0) schedule["at"] = now.AddMinutes(repeat).ToString("o"); else schedule["enabled"] = false;
        }

        private async Task SteerChat(HttpListenerContext context, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || !job.IsActive || job.Kind != "chat") { await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前没有可实时转向的任务" }, 409); return; }
            if (!SupportsLiveSteering(job)) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前命令行核心不支持运行中注入，请把新要求加入优先队列，当前轮结束后会执行。", ["code"] = "worker_steering_unsupported" }, 409); return; }
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var text = ((string)body["text"] ?? "").Trim();
            if (text.Length == 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "转向指令不能为空" }, 400); return; }
            var inputOffset = AppendChatInput(job, "方向调整：下面是当前最高优先级的新要求。请立即据此调整后续工作方向；已经完成且仍适用的结果可以保留。\n\n" + text);
            await WriteJsonAsync(context.Response, new JObject { ["ok"] = true, ["jobId"] = job.Id, ["inputOffset"] = inputOffset, ["live"] = true });
        }

        private static bool JobProcessAlive(Job job)
        {
            try
            {
                int pid;
                return File.Exists(job.PidPath) && int.TryParse(File.ReadAllText(job.PidPath).Trim(), out pid) && ProcessAlive(pid);
            }
            catch { return false; }
        }

        private static bool SupportsLiveSteering(Job job)
        {
            var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
            var harness = (string)request["workerHarness"] ?? "claude";
            return harness == "claude";
        }

        private static DateTimeOffset TerminalAt(Job job)
        {
            try
            {
                var status = JsonUtil.Read(job.StatusPath, new JObject()) as JObject ?? new JObject();
                DateTimeOffset parsed;
                if (DateTimeOffset.TryParse((string)status["finishedAt"], out parsed)) return parsed;
            }
            catch { }
            return job.StartedAtUtc == default(DateTimeOffset) ? DateTimeOffset.UtcNow : job.StartedAtUtc;
        }

        private static void RetireIdleWorker(Job job)
        {
            if (job == null || job.IsActive) return;
            try
            {
                if (job.Worker != null) job.Worker.Retire(job.State);
                else
                {
                    int pid;
                    if (File.Exists(job.PidPath) && int.TryParse(File.ReadAllText(job.PidPath).Trim(), out pid) && ProcessAlive(pid))
                        Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(8000);
                }
            }
            catch (Exception error) { CrashLog.Handled("IdleWorkerRetire:" + job.Id, error); }
            job.Worker = null;
            job.Process = null;
        }

        private static string WorkerReuseFingerprint(JObject payload, JObject provider, string model, string sourceWorkspace,
            string permissionMode, string[] addDirs, JArray allowedTools, JObject toolRuntimePolicy, JObject trustedRuntime, string claudeSessionId,
            string memoryText)
        {
            var text = provider["text"] as JObject ?? new JObject();
            var manifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["sourceWorkspace"] = Path.GetFullPath(sourceWorkspace ?? AppPaths.Workspace),
                ["claudeSessionId"] = claudeSessionId ?? "",
                ["model"] = model ?? "",
                ["workerHarness"] = AgentWorkerSdk.SelectedHarness(payload),
                ["codexExecutable"] = AgentWorkerSdk.SelectedHarness(payload) == "codex" ? AgentWorkerSdk.FindCodexExecutable() : "",
                ["dshEntry"] = AgentWorkerSdk.SelectedHarness(payload) == "dsh" ? AgentWorkerSdk.FindDshEntry() : "",
                ["dshNode"] = AgentWorkerSdk.SelectedHarness(payload) == "dsh" ? AgentWorkerSdk.FindNodeExecutable() : "",
                ["workerModel"] = (string)payload["workerModel"] ?? "",
                ["effort"] = (string)payload["effort"] ?? "high",
                ["permissionMode"] = permissionMode ?? "readonly",
                ["maxTurns"] = Math.Max(10, Math.Min(500, (int?)payload["maxTurns"] ?? 100)),
                ["provider"] = new JObject
                {
                    ["id"] = provider["id"], ["protocol"] = text["protocol"], ["baseUrl"] = text["baseUrl"],
                    ["authStyle"] = provider["authStyle"], ["tokenEncrypted"] = provider["tokenEncrypted"]
                },
                ["addDirs"] = new JArray((addDirs ?? new string[0]).Select(Path.GetFullPath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                ["allowedTools"] = new JArray((allowedTools ?? new JArray()).Values<string>().OrderBy(value => value, StringComparer.Ordinal)),
                ["disallowedTools"] = new JArray((payload["disallowedTools"] as JArray ?? new JArray()).Values<string>().OrderBy(value => value, StringComparer.Ordinal)),
                ["toolRuntimePolicy"] = toolRuntimePolicy == null ? new JObject() : toolRuntimePolicy.DeepClone(),
                ["trustedInstructionSha256"] = Sha256Text((string)trustedRuntime?["instructionText"] ?? ""),
                ["workspaceMemorySha256"] = Sha256Text(memoryText ?? ""),
                ["trustedMcpConfigs"] = trustedRuntime?["mcpConfigs"]?.DeepClone() ?? new JArray(),
                ["trustedAgents"] = trustedRuntime?["agents"]?.DeepClone() ?? new JObject()
            };
            return Sha256Text(manifest.ToString(Formatting.None));
        }

        private void RecordWorkspaceMemoryContext(string runId, string taskId, PreparedContext prepared)
        {
            if (prepared == null) return;
            foreach (var memory in prepared.WorkspaceMemories.OfType<JObject>())
            {
                _eventStore.RecordContext(runId, taskId, "workspace-memory", (string)memory["title"] ?? "工作区记忆",
                    (string)memory["content"] ?? "", new JObject
                    {
                        ["memoryId"] = memory["id"], ["source"] = memory["source"] ?? "manual",
                        ["countedInPrompt"] = true, ["scope"] = "workspace"
                    });
            }
        }

        private long AppendChatInput(Job job, string text)
        {
            lock (job.StateGate)
            {
                job.TurnStartSeq = job.EventCount();
                var input = new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text ?? "" }) } };
                File.AppendAllText(job.InputPath, input.ToString(Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
                JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = "running", ["message"] = "Claude Code 正在处理输入", ["startedAt"] = ProviderStore.NowIso() });
                job.IsActive = true; job.Busy = true; job.State = "running";
                job.Runtime.BeginTurn(job.TurnStartSeq, "Claude Code 正在处理输入");
            }
            NotifyState();
            return new FileInfo(job.InputPath).Length;
        }

        private void ReconcileActiveJobs()
        {
            if (!IsRunning || Interlocked.Exchange(ref _jobChecking, 1) != 0) return;
            try
            {
                InjectBackgroundLoopFailure("job", ref TestJobLoopFailureInjected);
                foreach (var job in _jobs.Values.Where(value => value.Kind == "chat" && value.IsActive).ToArray())
                {
                    try
                    {
                        var status = JsonUtil.Read(job.StatusPath, new JObject { ["state"] = "running" }) as JObject ?? new JObject { ["state"] = "running" };
                        TryFinalizeTerminalJob(job, status, (string)status["state"] ?? "running", true);
                    }
                    catch (Exception error) { CrashLog.Handled("JobReconcile:" + job.Id, error); }
                }
                var idle = _jobs.Values.Where(value => value.Kind == "chat" && !value.IsActive && value.Persistent && JobProcessAlive(value))
                    .OrderByDescending(TerminalAt).ToArray();
                for (var index = 0; index < idle.Length; index++)
                {
                    var terminalAt = TerminalAt(idle[index]);
                    if (index >= MaxIdleWorkers || DateTimeOffset.UtcNow - terminalAt.ToUniversalTime() >= IdleWorkerRetention)
                        RetireIdleWorker(idle[index]);
                }
            }
            catch (Exception error) { CrashLog.Handled("JobReconcileLoop", error); }
            finally { Interlocked.Exchange(ref _jobChecking, 0); }
        }

        private bool TryFinalizeTerminalJob(Job job, JObject status, string state, bool notifyBackground)
        {
            state = (state ?? "running").Trim().ToLowerInvariant();
            if (state != JobStates.Completed && state != JobStates.Failed && state != JobStates.Cancelled) return false;
            var error = ""; var finalized = false;
            var durationMs = job.ElapsedMilliseconds();
            lock (job.StateGate)
            {
                if (!job.IsActive) return false;
                if (state == JobStates.Completed)
                {
                    var inputState = JsonUtil.Read(job.InputStatePath, new JObject()) as JObject ?? new JObject();
                    var sentOffset = (long?)inputState["sentOffset"] ?? 0L;
                    var completedOffset = (long?)inputState["completedOffset"] ?? 0L;
                    if (sentOffset > completedOffset) return false;
                }
                if (state == JobStates.Failed)
                {
                    error = File.Exists(job.ErrorPath) ? ReadSharedText(job.ErrorPath) : "";
                    if (string.IsNullOrWhiteSpace(error)) error = (string)status["message"] ?? "请求失败，但上游未返回错误详情";
                    RecordProviderTerminal(job, status, false, error);
                    if (status["fallbackDecision"] == null)
                    {
                        var fallbackDecision = BuildProviderFallbackDecision(job, error);
                        if (fallbackDecision != null)
                        {
                            status["fallbackDecision"] = fallbackDecision;
                            JsonUtil.WriteAtomic(job.StatusPath, status);
                            _eventStore.AppendWorkerEvent(job.Id, job.SessionId,
                                new JObject { ["type"] = "system", ["subtype"] = "provider_fallback", ["decision"] = fallbackDecision }.ToString(Formatting.None),
                                "provider-fallback:" + job.Id);
                        }
                    }
                }
                else if (state == JobStates.Completed) RecordProviderTerminal(job, status, true, "");
                else error = (string)status["message"] ?? "任务已取消";

                job.State = state; job.IsActive = false; job.Busy = false;
                job.Runtime.Transition(state, (string)status["message"] ?? error,
                    new JObject { ["eventSeq"] = job.EventCount(), ["exitCode"] = status["exitCode"], ["reconciledByHost"] = true });
                _eventStore.UpdateRunState(job.Id, state, job.Process == null ? 0 : job.Process.Id, status.ToString(Formatting.None));
                finalized = true;
            }
            if (!finalized) return false;
            OpenAiAdapter.CancelRun(job.Id);
            NativeMetrics.RecordRunEnd(job.Id, state, durationMs);
            NotifyState();
            if (notifyBackground && _window != null && !IsScheduleRun(job))
            {
                var title = state == JobStates.Completed ? "Claude Code 任务完成" : state == JobStates.Cancelled ? "Claude Code 任务已取消" : "Claude Code 任务失败";
                var message = state == JobStates.Completed ? "后台 Agent 已完成，可以重新打开工作台查看结果。" : Limit(error, 220);
                _window.ShowBackgroundNotification(title, message);
            }
            return true;
        }

        private static bool IsScheduleRun(Job job)
        {
            try
            {
                var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
                return ((string)request["requestId"] ?? "").StartsWith("schedule:", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private async Task PollChat(HttpListenerContext context, string id)
        {
            var response = context.Response;
            id = SafeId(id);
            Job job;
            if (!_jobs.TryGetValue(id, out job))
            {
                var runDir = Path.Combine(AppPaths.Runs, id);
                if (Directory.Exists(runDir))
                {
                    try
                    {
                        job = Job.Chat(id, runDir, _eventStore);
                        var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
                        var recoveredStatus = JsonUtil.Read(job.StatusPath, new JObject { ["state"] = JobStates.Failed }) as JObject ?? new JObject();
                        job.SessionId = (string)request["guiSessionId"] ?? (string)request["sessionId"] ?? "";
                        job.Workspace = (string)request["workspace"] ?? AppPaths.Workspace;
                        job.Model = (string)request["model"] ?? "";
                        job.State = (string)recoveredStatus["state"] ?? JobStates.Failed;
                        job.IsActive = IsActiveJobState(job.State);
                        job.Busy = job.IsActive; job.Persistent = true; job.Recovered = true;
                        _jobs[id] = job;
                    }
                    catch { job = null; }
                }
                if (job == null) { await WriteJsonAsync(response, new JObject { ["error"] = "任务不存在" }, 404); return; }
            }
            long after; if (!long.TryParse(context.Request.QueryString["after"], out after)) after = 0;
            long nextSeq;
            var events = job.ReadEvents(after, 240, out nextSeq);
            var lines = new JArray(events.OfType<JObject>().Select(value => value["payload"]));
            var status = JsonUtil.Read(job.StatusPath, new JObject { ["state"] = "running" }) as JObject
                ?? new JObject { ["state"] = "running" };
            if (!File.Exists(job.StatusPath) && job.Process != null)
            {
                try
                {
                    if (job.Process.HasExited)
                    {
                        status = new JObject
                        {
                            ["state"] = "failed", ["exitCode"] = job.Process.ExitCode,
                            ["message"] = "Claude Code Worker 意外退出，未生成状态文件。"
                        };
                    }
                }
                catch { }
            }
            var error = "";
            var state = (string)status["state"] ?? "running";
            if (state == "completed")
            {
                var inputState = JsonUtil.Read(job.InputStatePath, new JObject()) as JObject ?? new JObject();
                var sentOffset = (long?)inputState["sentOffset"] ?? 0L; var completedOffset = (long?)inputState["completedOffset"] ?? 0L;
                if (sentOffset > completedOffset)
                {
                    status["state"] = "running"; status["message"] = "正在处理已注入的下一条方向"; state = "running";
                }
            }
            var terminalState = state;
            if (terminalState == JobStates.Completed || terminalState == JobStates.Failed || terminalState == JobStates.Cancelled)
            {
                TryFinalizeTerminalJob(job, status, terminalState, false);
                if (terminalState == JobStates.Failed)
                {
                    error = File.Exists(job.ErrorPath) ? ReadSharedText(job.ErrorPath) : "";
                    if (string.IsNullOrWhiteSpace(error)) error = (string)status["message"] ?? "请求失败，但上游未返回错误详情";
                }
            }
            var responseStatus = (JObject)status.DeepClone();
            if ((terminalState == JobStates.Completed || terminalState == JobStates.Failed || terminalState == JobStates.Cancelled) && nextSeq < job.EventCount())
            {
                responseStatus["terminalState"] = terminalState;
                responseStatus["state"] = "running";
                responseStatus["message"] = "正在整理剩余流式输出";
            }
            job.State = terminalState;
            await WriteJsonAsync(response, new JObject { ["events"] = events, ["lines"] = lines, ["nextSeq"] = nextSeq, ["status"] = responseStatus, ["jobState"] = job.Runtime.Snapshot, ["error"] = error });
        }

        private void RecordProviderTerminal(Job job, JObject status, bool success, string error)
        {
            if ((bool?)status["providerHealthRecorded"] == true) return;
            try
            {
                var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
                var providerId = (string)request["provider"]?["id"] ?? "";
                var model = (string)request["model"] ?? job.Model ?? "";
                if (providerId.Length == 0 || model.Length == 0) return;
                var classification = success ? new JObject { ["kind"] = "none", ["label"] = "成功", ["retryable"] = false, ["cooldownSeconds"] = 0 } : ClassifyProviderFailure(error);
                long latency = 0;
                try
                {
                    var started = File.Exists(job.InputPath) ? File.GetLastWriteTimeUtc(job.InputPath) : File.GetCreationTimeUtc(job.RequestPath);
                    latency = Math.Max(0L, Math.Min(86400000L, (long)(DateTime.UtcNow - started).TotalMilliseconds));
                }
                catch { }
                var health = _eventStore.RecordProviderOutcome(providerId, model, success, (string)classification["kind"], error, latency,
                    (int?)classification["cooldownSeconds"] ?? 0, "claude-worker-result");
                status["providerHealthRecorded"] = true;
                status["providerHealth"] = health;
                status["failureClassification"] = classification;
                JsonUtil.WriteAtomic(job.StatusPath, status);
            }
            catch (Exception healthError) { CrashLog.Handled("ProviderHealth", healthError); }
        }

        private static JObject ClassifyProviderFailure(string value)
        {
            var text = (value ?? "").ToLowerInvariant();
            string kind; string label; bool retryable; int cooldown;
            if (ContainsAny(text, "401", "403", "unauthorized", "forbidden", "invalid api key", "authentication", "鉴权", "令牌无效"))
            { kind = "authentication"; label = "鉴权失败"; retryable = false; cooldown = 900; }
            else if (ContainsAny(text, "429", "rate limit", "too many requests", "限流", "频率限制"))
            { kind = "rate_limit"; label = "请求限流"; retryable = true; cooldown = 60; }
            else if (ContainsAny(text, "insufficient_quota", "quota", "billing", "balance", "余额", "额度不足"))
            { kind = "quota"; label = "额度或余额不足"; retryable = false; cooldown = 300; }
            else if (ContainsAny(text, "context length", "context window", "maximum context", "too many tokens", "上下文", "token 过多"))
            { kind = "context_limit"; label = "Context 超限"; retryable = false; cooldown = 0; }
            else if (ContainsAny(text, "model not found", "unknown model", "unsupported model", "does not exist", "模型不存在", "模型不可用") ||
                text.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("not available", StringComparison.OrdinalIgnoreCase) >= 0)
            { kind = "model_unavailable"; label = "模型不可用"; retryable = false; cooldown = 300; }
            else if (ContainsAny(text, "timeout", "timed out", "deadline", "超时"))
            { kind = "timeout"; label = "上游超时"; retryable = true; cooldown = 30; }
            else if (ContainsAny(text, "connection", "dns", "socket", "network", "econn", "连接", "网络"))
            { kind = "network"; label = "网络连接失败"; retryable = true; cooldown = 30; }
            else if (ContainsAny(text, "500", "502", "503", "504", "upstream", "service unavailable", "上游"))
            { kind = "upstream"; label = "上游服务异常"; retryable = true; cooldown = 45; }
            else { kind = "unknown"; label = "未分类失败"; retryable = true; cooldown = 20; }
            return new JObject { ["kind"] = kind, ["label"] = label, ["retryable"] = retryable, ["cooldownSeconds"] = cooldown };
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            return values.Any(value => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private JObject BuildProviderFallbackDecision(Job job, string failure)
        {
            try
            {
                var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
                var currentProviderId = (string)request["provider"]?["id"] ?? "";
                var currentProtocol = (string)request["provider"]?["protocol"] ?? "";
                var prompt = (string)request["prompt"] ?? "";
                var visualExtensions = new HashSet<string>(new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".pdf" }, StringComparer.OrdinalIgnoreCase);
                var requiresVision = (request["attachments"] as JArray ?? new JArray()).Any(value => visualExtensions.Contains(Path.GetExtension((string)value ?? "")));
                var permissionMode = ((string)request["permissionMode"] ?? "readonly").ToLowerInvariant();
                var requiresTools = permissionMode == "scoped" || permissionMode == "edit" || permissionMode == "agent" || permissionMode == "full";
                var health = _eventStore.ListProviderHealth().OfType<JObject>().ToArray();
                var ranked = new List<JObject>();
                foreach (var provider in _providers.AllPublic().OfType<JObject>())
                {
                    var id = (string)provider["id"] ?? "";
                    var text = provider["text"] as JObject;
                    var models = text?["models"] as JArray ?? new JArray();
                    if (id.Length == 0 || string.Equals(id, currentProviderId, StringComparison.OrdinalIgnoreCase) ||
                        !((bool?)text?["enabled"] ?? false) || models.Count == 0) continue;
                    var modelCandidates = new List<JObject>();
                    foreach (var modelValue in models.Take(80))
                    {
                        var model = ((string)modelValue ?? "").Trim(); if (model.Length == 0) continue;
                        string visionEvidence; var vision = SupportsVision(provider, model, out visionEvidence);
                        if (requiresVision && !vision) continue;
                        string toolEvidence; var toolSupport = ToolSupport(provider, model, out toolEvidence);
                        if (requiresTools && toolSupport == false) continue;
                        var budget = ContextBudgetPlanner.Plan(provider, model, prompt, "", "", false);
                        if (string.Equals((string)budget["decision"], "blocked", StringComparison.OrdinalIgnoreCase)) continue;
                        var itemHealth = health.FirstOrDefault(item => string.Equals((string)item["providerId"], id, StringComparison.OrdinalIgnoreCase) && string.Equals((string)item["model"], model, StringComparison.OrdinalIgnoreCase))
                            ?? health.FirstOrDefault(item => string.Equals((string)item["providerId"], id, StringComparison.OrdinalIgnoreCase) && (string)item["model"] == "*");
                        var state = (string)itemHealth?["state"] ?? "unknown"; var available = (bool?)itemHealth?["available"] ?? true;
                        var score = state == "healthy" ? 90 : state == "reachable" ? 72 : state == "unknown" ? 58 : state == "degraded" || state == "probe_failed" ? 34 : 8;
                        var reasons = new JArray();
                        reasons.Add(state == "healthy" ? "最近一次模型执行成功" : state == "reachable" ? "模型列表接口最近可达" : state == "cooling" ? "失败后仍在冷却期" : state == "degraded" || state == "probe_failed" ? "最近存在失败记录" : "尚无运行健康证据");
                        if (string.Equals((string)text["protocol"], currentProtocol, StringComparison.OrdinalIgnoreCase)) { score += 4; reasons.Add("与当前请求使用相同协议"); }
                        if (requiresVision) { score += visionEvidence == "provider-metadata" ? 14 : 7; reasons.Add(visionEvidence == "provider-metadata" ? "接口元数据确认视觉能力" : "模型与协议推断支持视觉输入"); }
                        if (toolSupport == true) { score += 12; reasons.Add("接口元数据确认 Tool calling"); }
                        else if (toolSupport == null) { score -= 2; reasons.Add("Tool calling 能力尚未验证"); }
                        if ((string)budget["capacityEvidence"] == "provider-metadata") { score += 6; reasons.Add("Context Window 来自接口元数据"); }
                        if (available) score += 5; else score -= 70;
                        score += (int)Math.Min(10L, (long?)itemHealth?["successCount"] ?? 0L);
                        modelCandidates.Add(new JObject
                        {
                            ["model"] = model, ["score"] = score, ["healthState"] = state, ["available"] = available,
                            ["lastLatencyMs"] = (long?)itemHealth?["lastLatencyMs"] ?? 0L,
                            ["cooldownUntil"] = (string)itemHealth?["cooldownUntil"] ?? "",
                            ["cooldownRemainingSeconds"] = (long?)itemHealth?["cooldownRemainingSeconds"] ?? 0L,
                            ["failureKind"] = (string)itemHealth?["failureKind"] ?? "",
                            ["inputBudgetTokens"] = (long?)budget["inputBudgetTokens"] ?? 0L,
                            ["capacityEvidence"] = (string)budget["capacityEvidence"] ?? "fallback", ["visionEvidence"] = visionEvidence, ["toolEvidence"] = toolEvidence,
                            ["reasons"] = reasons
                        });
                    }
                    var ordered = modelCandidates.OrderByDescending(item => (int?)item["score"] ?? 0).ThenBy(item => (string)item["model"]).ToArray();
                    if (ordered.Length == 0) continue;
                    var best = ordered[0];
                    ranked.Add(new JObject
                    {
                        ["providerId"] = id, ["name"] = (string)provider["name"] ?? id,
                        ["protocol"] = (string)text["protocol"] ?? "unknown",
                        ["models"] = new JArray(ordered.Take(4).Select(item => (string)item["model"])),
                        ["score"] = best["score"], ["healthState"] = best["healthState"], ["available"] = best["available"],
                        ["lastLatencyMs"] = best["lastLatencyMs"], ["cooldownUntil"] = best["cooldownUntil"],
                        ["cooldownRemainingSeconds"] = best["cooldownRemainingSeconds"], ["failureKind"] = best["failureKind"],
                        ["inputBudgetTokens"] = best["inputBudgetTokens"], ["capacityEvidence"] = best["capacityEvidence"],
                        ["rankReasons"] = best["reasons"]
                    });
                }
                var candidates = new JArray(ranked.OrderByDescending(item => (bool?)item["available"] ?? true).ThenByDescending(item => (int?)item["score"] ?? 0).Take(8));
                if (candidates.Count == 0) return null;
                var classification = ClassifyProviderFailure(failure);
                return new JObject
                {
                    ["strategy"] = "capability-health-ranked-user-confirmed", ["automatic"] = false,
                    ["currentProviderId"] = currentProviderId, ["classification"] = classification,
                    ["reason"] = Limit(SecretRedactor.Redact(failure), 600), ["requiresVision"] = requiresVision, ["requiresTools"] = requiresTools,
                    ["candidateCount"] = candidates.Count, ["candidates"] = candidates, ["createdAt"] = ProviderStore.NowIso()
                };
            }
            catch (Exception error) { CrashLog.Handled("ProviderFallback", error); return null; }
        }

        private static bool SupportsVision(JObject provider, string model, out string evidence)
        {
            var capability = provider?["capabilities"]?["models"]?[model] as JObject;
            if (capability?["vision"]?.Type == JTokenType.Boolean)
            {
                evidence = "provider-metadata"; return (bool)capability["vision"];
            }
            var protocol = (string)provider?["text"]?["protocol"] ?? "";
            if (string.Equals(protocol, "openai", StringComparison.OrdinalIgnoreCase)) { evidence = "adapter-transport"; return false; }
            var normalized = (model ?? "").ToLowerInvariant();
            var patterns = new[] { "claude-", "vision", "-vl", "vl-", "vlm", "omni", "pixtral", "llava", "internvl", "minicpm-v", "glm-4v", "glm-4.5v", "kimi-vl" };
            evidence = "model-name"; return patterns.Any(pattern => normalized.Contains(pattern));
        }

        private static bool? ToolSupport(JObject provider, string model, out string evidence)
        {
            var capability = provider?["capabilities"]?["models"]?[model] as JObject;
            if (capability?["tools"]?.Type == JTokenType.Boolean)
            {
                evidence = "provider-metadata";
                return (bool)capability["tools"];
            }
            evidence = "unknown";
            return null;
        }

        private static bool ClaudeSessionExists(string workspace, string sessionId)
        {
            try { return File.Exists(ClaudeTranscriptPath(workspace, sessionId)); }
            catch { return false; }
        }

        private static bool EnsureClaudeSessionForWorker(string sourceWorkspace, string workerWorkspace, string sessionId)
        {
            try
            {
                var target = ClaudeTranscriptPath(workerWorkspace, sessionId);
                if (File.Exists(target))
                {
                    WriteTranscriptMirrorDescriptor(target, sourceWorkspace, workerWorkspace, sessionId);
                    return true;
                }
                var source = ClaudeTranscriptPath(sourceWorkspace, sessionId);
                if (!File.Exists(source)) return false;
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return true;

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(input, new UTF8Encoding(false, true), true, 8192))
                    using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            JObject entry;
                            try
                            {
                                entry = JObject.Parse(line);
                                RewriteWorkspaceReferences(entry, Path.GetFullPath(sourceWorkspace), Path.GetFullPath(workerWorkspace));
                                entry["workbenchSourceCwd"] = Path.GetFullPath(sourceWorkspace);
                                writer.WriteLine(entry.ToString(Formatting.None));
                            }
                            catch (Newtonsoft.Json.JsonException)
                            {
                                writer.WriteLine(line);
                            }
                        }
                    }
                    if (File.Exists(target))
                    {
                        WriteTranscriptMirrorDescriptor(target, sourceWorkspace, workerWorkspace, sessionId);
                        return true;
                    }
                    File.Move(temporary, target);
                    WriteTranscriptMirrorDescriptor(target, sourceWorkspace, workerWorkspace, sessionId);
                    return true;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (Exception error)
            {
                CrashLog.Handled("TranscriptMirror", error);
                return false;
            }
        }

        private static void WriteTranscriptMirrorDescriptor(string transcriptPath, string sourceWorkspace, string workerWorkspace, string sessionId)
        {
            JsonUtil.WriteAtomic(transcriptPath + ".workbench.json", new JObject
            {
                ["schemaVersion"] = 1,
                ["sessionId"] = sessionId,
                ["sourceWorkspace"] = Path.GetFullPath(sourceWorkspace),
                ["workerWorkspace"] = Path.GetFullPath(workerWorkspace),
                ["updatedAt"] = ProviderStore.NowIso()
            });
        }

        private static void RewriteWorkspaceReferences(JToken token, string sourceWorkspace, string workerWorkspace)
        {
            if (token == null) return;
            if (token.Type == JTokenType.String)
            {
                var value = (string)token ?? "";
                token.Replace(ReplaceOrdinalIgnoreCase(value, sourceWorkspace, workerWorkspace));
                return;
            }
            foreach (var child in token.Children().ToArray()) RewriteWorkspaceReferences(child, sourceWorkspace, workerWorkspace);
        }

        private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldValue)) return value ?? "";
            var result = new StringBuilder(value.Length);
            var offset = 0;
            while (offset < value.Length)
            {
                var index = value.IndexOf(oldValue, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) { result.Append(value, offset, value.Length - offset); break; }
                result.Append(value, offset, index - offset);
                result.Append(newValue);
                offset = index + oldValue.Length;
            }
            return result.ToString();
        }

        private static string ClaudeTranscriptPath(string workspace, string sessionId)
        {
            Guid parsedSessionId;
            if (!Guid.TryParse(sessionId, out parsedSessionId)) throw new ArgumentException("Claude session ID 无效", "sessionId");
            var fullWorkspace = Path.GetFullPath(workspace ?? "");
            var projectKey = new string(fullWorkspace.Select(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '-').ToArray());
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", projectKey, parsedSessionId.ToString() + ".jsonl");
        }

        private static int DeleteClaudeSessionCopies(string sessionId)
        {
            Guid parsedSessionId;
            if (!Guid.TryParse(sessionId, out parsedSessionId)) return 0;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return 0;
            var fileName = parsedSessionId.ToString() + ".jsonl";
            var deleted = 0;
            string[] candidates;
            try { candidates = Directory.GetFiles(root, fileName, SearchOption.AllDirectories); }
            catch { return 0; }
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(candidate);
                    var relative = full.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                    if (relative.StartsWith("..", StringComparison.Ordinal) || !string.Equals(Path.GetFileName(full), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                    File.Delete(full);
                    var descriptor = full + ".workbench.json";
                    if (File.Exists(descriptor)) File.Delete(descriptor);
                    deleted++;
                }
                catch (Exception error) { CrashLog.Handled("DeleteTranscript", error); }
            }
            return deleted;
        }

        private static string[] AgentRoots()
        {
            try
            {
                return DriveInfo.GetDrives().Where(drive =>
                {
                    try { return drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable); }
                    catch { return false; }
                }).Select(drive => drive.RootDirectory.FullName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch { return new string[0]; }
        }

        private void ConfigurePermissionBroker(JObject request, string runDir, string jobId)
        {
            var core = (string)request["workerHarness"] ?? "claude";
            if (core == "codex" || core == "dsh")
            {
                request["permissionBrokerEnabled"] = false;
                request["permissionMcpConfig"] = "";
                request["mcpConfigs"] = new JArray();
                request.Remove("claudeCodeIsolation");
                request["coreIsolation"] = new JObject { ["core"] = core, ["permissionMode"] = request["permissionMode"], ["enforcedBy"] = "selected-cli", ["workbenchMcpApplied"] = false, ["workbenchAgentDefinitionsApplied"] = false };
                return;
            }
            var mode = ((string)request["permissionMode"] ?? "readonly").ToLowerInvariant();
            var permissionConfig = Path.Combine(runDir, "permissions.mcp.json");
            var servers = new JObject();
            var permissionBrokerEnabled = new[] { "manual", "scoped", "edit", "agent" }.Contains(mode);
            if (permissionBrokerEnabled) servers["gui_permissions"] = new JObject
            {
                ["command"] = Assembly.GetExecutingAssembly().Location,
                ["args"] = new JArray("--permission-mcp"),
                ["env"] = new JObject
                {
                    ["CLAUDE_GUI_PERMISSION_BASE"] = BaseUrl,
                    ["CLAUDE_GUI_PERMISSION_SECRET_PROTECTED"] = SecretStore.Protect(_secret),
                    ["CLAUDE_GUI_PERMISSION_JOB"] = jobId,
                    ["CLAUDE_GUI_PERMISSION_TIMEOUT_SECONDS"] = (int?)request["toolRuntimePolicy"]?["approvalTimeoutSeconds"] ?? 600
                }
            };
            // Always provide an explicit MCP source. With --bare + --strict-mcp-config,
            // project/user .mcp.json files can never enter the Worker implicitly.
            JsonUtil.WriteAtomic(permissionConfig, new JObject { ["mcpServers"] = servers });
            request["permissionMcpConfig"] = permissionConfig;
            request["permissionBrokerEnabled"] = permissionBrokerEnabled;
            var mcpConfigs = new JArray(permissionConfig);
            foreach (var value in (request["trustedMcpConfigs"] as JArray ?? new JArray()).Values<string>())
            {
                if (string.IsNullOrWhiteSpace(value) || !File.Exists(value)) continue;
                if (!mcpConfigs.Values<string>().Contains(value, StringComparer.OrdinalIgnoreCase)) mcpConfigs.Add(value);
            }
            request["mcpConfigs"] = mcpConfigs;
            request["claudeCodeIsolation"] = new JObject
            {
                ["mode"] = "bare",
                ["implicitProjectSettings"] = false,
                ["implicitHooks"] = false,
                ["implicitPlugins"] = false,
                ["implicitMcp"] = false,
                ["explicitMcpConfigs"] = mcpConfigs.DeepClone(),
                ["trustedProjectMcpCount"] = Math.Max(0, mcpConfigs.Count - 1)
            };
        }

        private async Task StopChat(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job)) { await WriteJsonAsync(response, new JObject { ["stopped"] = false }); return; }
            StopJob(job);
            NotifyState();
            await WriteJsonAsync(response, new JObject { ["stopped"] = true });
        }

        private async Task PauseChat(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || job.Worker == null || !job.Worker.Pause()) { await WriteJsonAsync(response, new JObject { ["paused"] = false, ["error"] = "当前任务无法暂停" }, 409); return; }
            job.State = JobStates.Paused; job.Runtime.Transition(JobStates.Paused, "任务已暂停，Worker 进程保留在内存中");
            JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = JobStates.Paused, ["message"] = "任务已暂停", ["pausedAt"] = ProviderStore.NowIso() });
            NotifyState();
            await WriteJsonAsync(response, new JObject { ["paused"] = true, ["jobId"] = id });
        }

        private async Task ResumeChat(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || job.Worker == null || !job.Worker.Resume()) { await WriteJsonAsync(response, new JObject { ["resumed"] = false, ["error"] = "当前任务无法继续" }, 409); return; }
            job.State = JobStates.Running; job.Runtime.Transition(JobStates.Running, "任务已继续");
            JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = JobStates.Running, ["message"] = "任务已继续", ["resumedAt"] = ProviderStore.NowIso() });
            NotifyState();
            await WriteJsonAsync(response, new JObject { ["resumed"] = true, ["jobId"] = id });
        }

        private async Task StartImage(HttpListenerContext context)
        {
            if (IsUpdateMaintenance)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "工作台正在安装更新，暂不接受新的生图任务", ["code"] = "update_maintenance" }, 503); return;
            }
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var provider = _providers.Get((string)payload["providerId"] ?? "");
            if (provider == null || !((bool?)provider["image"]?["enabled"] ?? false))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 API 没有启用图像生成能力" }, 400); return;
            }
            var model = ((string)payload["model"] ?? "").Trim();
            var configuredModels = provider["image"]?["models"] as JArray ?? new JArray();
            if (model.Length == 0 || configuredModels.Count > 0 && !configuredModels.Values<string>().Any(value => string.Equals(value, model, StringComparison.Ordinal)))
            { await WriteJsonAsync(context.Response, new JObject { ["error"] = "请选择当前 API 已保存的生图模型" }, 400); return; }
            var prompt = ((string)payload["prompt"] ?? "").Trim();
            if (prompt.Length == 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "图片描述不能为空" }, 400); return; }
            var id = Guid.NewGuid().ToString();
            var job = Job.Image(id);
            job.Model = model;
            job.SessionId = ((string)payload["sessionId"] ?? id).Trim(); if (job.SessionId.Length == 0) job.SessionId = id;
            job.Workspace = ((string)payload["workspace"] ?? AppPaths.Workspace).Trim();
            _jobs[id] = job;
            _eventStore.UpsertTask(job.SessionId, job.Workspace, Limit((string)payload["prompt"] ?? "生成图片", 80), JobStates.Running, false, "{}");
            _eventStore.UpsertRun(id, job.SessionId, "image:" + id, (string)payload["model"] ?? "", job.Workspace, JobStates.Running, 0);
            NotifyState();
            var imageTask = Task.Run(() => RunImageAsync(job, provider, model, prompt, (string)payload["size"] ?? "1024x1024"));
            await WriteJsonAsync(context.Response, new JObject { ["jobId"] = id });
        }

        private async Task RunImageAsync(Job job, JObject provider, string model, string prompt, string size)
        {
            var providerToken = "";
            try
            {
                job.Cancellation.CancelAfter(ImageRequestTimeout);
                var cancellation = job.Cancellation.Token;
                var token = providerToken = _providers.Token((string)provider["id"]);
                var config = provider["image"] as JObject ?? new JObject();
                var url = CapabilityEndpoint((string)config["baseUrl"], "/v1/images/generations");
                var configuredAuthStyle = ((string)provider["authStyle"] ?? "auto").Trim().ToLowerInvariant();
                var authStyles = configuredAuthStyle == "auto" ? new[] { "bearer", "x-api-key" } : new[] { configuredAuthStyle };
                JObject body = null;
                for (var authIndex = 0; authIndex < authStyles.Length; authIndex++)
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        OpenAiAdapter.ApplyAuth(request, token, authStyles[authIndex]);
                        request.Content = new StringContent(new JObject
                        {
                            ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["n"] = 1, ["response_format"] = "b64_json"
                        }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                        using (var result = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation))
                        {
                            var text = await OpenAiAdapter.ReadContentLimitedAsync(result.Content, MaximumImageResponseBytes,
                                ImageRequestTimeout, cancellation, "生图接口响应正文读取超时");
                            var retryAuth = configuredAuthStyle == "auto" && authIndex + 1 < authStyles.Length &&
                                (result.StatusCode == HttpStatusCode.Unauthorized || result.StatusCode == HttpStatusCode.Forbidden);
                            if (retryAuth) continue;
                            if (!result.IsSuccessStatusCode)
                                throw new InvalidOperationException("上游 HTTP " + (int)result.StatusCode + "：" + ModelValidationError(text, token));
                            try { body = JObject.Parse(text); }
                            catch { throw new InvalidDataException("生图接口响应不是有效 JSON"); }
                            break;
                        }
                    }
                }
                if (body == null) throw new InvalidOperationException("没有可用的生图 API 鉴权方式");
                var item = body["data"]?[0] as JObject ?? new JObject();
                byte[] imageBytes;
                var base64 = (string)item["b64_json"];
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    try { imageBytes = Convert.FromBase64String(base64); }
                    catch { throw new InvalidDataException("生图接口返回的 b64_json 不是有效 Base64"); }
                    if (imageBytes.Length > MaximumDownloadedImageBytes) throw new InvalidDataException("生成图片超过 24 MB 安全上限");
                }
                else if (!string.IsNullOrWhiteSpace((string)item["url"])) imageBytes = await DownloadImageAsync((string)item["url"], cancellation);
                else throw new InvalidOperationException("图像接口没有返回 b64_json 或 url");
                cancellation.ThrowIfCancellationRequested();
                if (!job.IsActive || job.State == JobStates.Cancelled) return;
                var extension = ImageExtension(imageBytes);
                if (extension.Length == 0) throw new InvalidDataException("生图接口返回的数据不是受支持的 PNG、JPEG、WebP 或 GIF 图片");
                Directory.CreateDirectory(AppPaths.Images);
                var output = Path.Combine(AppPaths.Images, "生成图片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + job.Id.Substring(0, 8) + extension);
                File.WriteAllBytes(output, imageBytes);
                job.OutputImage = output;
                job.Usage = body["usage"];
                var mime = ImageMimeType(extension);
                _eventStore.RecordArtifact(job.SessionId, job.Id, "generated-image", output, mime, new JObject { ["model"] = model, ["size"] = size, ["providerId"] = provider["id"] });
                _eventStore.UpdateRunState(job.Id, JobStates.Completed, 0, new JObject { ["outputPath"] = output }.ToString(Formatting.None));
                job.State = JobStates.Completed;
                job.IsActive = false;
                NotifyState();
            }
            catch (OperationCanceledException)
            {
                if (!job.IsActive || job.State == JobStates.Cancelled) return;
                FailImage(job, "生图请求超时（" + Math.Ceiling(ImageRequestTimeout.TotalSeconds) + " 秒），连接已取消");
            }
            catch (Exception error) { FailImage(job, SafeProviderDetail(error.Message, providerToken)); }
        }

        private async Task<byte[]> DownloadImageAsync(string value, CancellationToken cancellation)
        {
            Uri url;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("生图接口返回的图片 URL 不是有效的 HTTP(S) 地址");
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("图片下载 HTTP " + (int)response.StatusCode);
                return await ReadBytesLimitedAsync(response.Content, MaximumDownloadedImageBytes, ImageRequestTimeout, cancellation);
            }
        }

        private static async Task<byte[]> ReadBytesLimitedAsync(HttpContent content, int maximumBytes, TimeSpan idleTimeout, CancellationToken cancellation)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maximumBytes)
                throw new InvalidDataException("图片下载超过 24 MB 安全上限");
            using (var stream = await content.ReadAsStreamAsync())
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var readTask = stream.ReadAsync(buffer, 0, buffer.Length, cancellation);
                    Task completed;
                    using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        var delay = Task.Delay(idleTimeout, delayCancellation.Token);
                        completed = await Task.WhenAny(readTask, delay);
                        if (completed == readTask) delayCancellation.Cancel();
                    }
                    if (completed != readTask) { cancellation.ThrowIfCancellationRequested(); throw new TimeoutException("图片下载正文读取超时"); }
                    var count = await readTask;
                    if (count <= 0) break;
                    if (memory.Length + count > maximumBytes) throw new InvalidDataException("图片下载超过 24 MB 安全上限");
                    memory.Write(buffer, 0, count);
                }
                return memory.ToArray();
            }
        }

        private static string ImageExtension(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return "";
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
            if (Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ".webp";
            if (Encoding.ASCII.GetString(bytes, 0, 3) == "GIF") return ".gif";
            return "";
        }

        private static string ImageMimeType(string extension)
        {
            if (extension == ".jpg") return "image/jpeg";
            if (extension == ".webp") return "image/webp";
            if (extension == ".gif") return "image/gif";
            return "image/png";
        }

        private void FailImage(Job job, string message)
        {
            if (!job.IsActive || job.State == JobStates.Cancelled) return;
            job.Error = message; job.State = JobStates.Failed; job.IsActive = false;
            _eventStore.UpdateRunState(job.Id, JobStates.Failed, 0, new JObject { ["error"] = message }.ToString(Formatting.None));
            NotifyState();
        }

        private async Task StopImage(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || job.Kind != "image" || !job.IsActive)
            { await WriteJsonAsync(response, new JObject { ["stopped"] = false }); return; }
            StopJob(job); NotifyState();
            await WriteJsonAsync(response, new JObject { ["stopped"] = true, ["jobId"] = id });
        }

        private async Task PollImage(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job)) { await WriteJsonAsync(response, new JObject { ["error"] = "任务不存在" }, 404); return; }
            await WriteJsonAsync(response, new JObject
            {
                ["id"] = job.Id, ["state"] = job.State, ["error"] = job.Error, ["outputPath"] = job.OutputImage, ["usage"] = job.Usage
            });
        }

        private async Task SendImage(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || string.IsNullOrWhiteSpace(job.OutputImage) || !File.Exists(job.OutputImage))
            { await NotFound(response); return; }
            await WriteBytesAsync(response, File.ReadAllBytes(job.OutputImage), ImageMimeType(Path.GetExtension(job.OutputImage).ToLowerInvariant()));
        }

        private async Task OpenTarget(HttpListenerResponse response, string target)
        {
            string path;
            if (target == "workspace") path = AppPaths.Workspace;
            else if (target == "images") path = AppPaths.Images;
            else if (target == "data") path = AppPaths.Data;
            else if (target == "terminal")
            {
                path = Path.Combine(AppPaths.ClaudeRoot, "启动Claude中文.cmd");
                if (!File.Exists(path)) path = Directory.GetFiles(AppPaths.ClaudeRoot, "*.cmd").FirstOrDefault(value => Path.GetFileName(value).IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            else { await NotFound(response); return; }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("目标不存在", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = AppPaths.Workspace });
            await Ok(response);
        }

        private async Task HandleAdapter(HttpListenerContext context, string path, string method)
        {
            if (method == "HEAD")
            {
                context.Response.StatusCode = 404;
                context.Response.ContentLength64 = 0;
                return;
            }
            if (method != "POST") { await NotFound(context.Response); return; }
            var rest = path.Substring("/adapter/".Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0) { await NotFound(context.Response); return; }
            var id = rest.Substring(0, slash);
            var tail = rest.Substring(slash);
            var provider = _providers.Get(id);
            if (provider == null || (string)provider["text"]?["protocol"] != "openai") { await WriteJsonAsync(context.Response, new JObject { ["error"] = "Forbidden" }, 403); return; }
            var token = _providers.Token(id);
            var supplied = context.Request.Headers["x-api-key"] ?? RemoveBearer(context.Request.Headers["Authorization"]);
            if (!ConstantEquals(token, supplied)) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "Forbidden" }, 403); return; }
            var runId = "";
            var v1Index = tail.IndexOf("/v1/", StringComparison.OrdinalIgnoreCase);
            if (v1Index > 1)
            {
                var candidate = tail.Substring(1, v1Index - 1);
                Guid parsedRunId;
                if (Guid.TryParse(candidate, out parsedRunId))
                {
                    runId = parsedRunId.ToString();
                    tail = tail.Substring(v1Index);
                }
            }
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            if (tail == "/v1/messages/count_tokens") await WriteJsonAsync(context.Response, OpenAiAdapter.CountTokens(payload));
            else if (tail == "/v1/messages")
            {
                var failure = await OpenAiAdapter.HandleAsync(context, provider, token, payload, runId);
                if (failure != null && runId.Length > 0) FailAdapterRun(runId, failure);
            }
            else await NotFound(context.Response);
        }

        private void FailAdapterRun(string runId, JObject failure)
        {
            Job job;
            if (!_jobs.TryGetValue(runId, out job)) return;
            var message = (string)failure?["error"]?["message"] ?? "上游模型请求失败";
            var finalized = false;
            lock (job.StateGate)
            {
                if (!job.IsActive) return;
                try { JsonUtil.WriteAtomic(Path.Combine(job.RunDir, "adapter-error.json"), failure); } catch { }
                try { File.WriteAllText(job.ErrorPath, message, new UTF8Encoding(false)); } catch { }
                try
                {
                    if (job.Worker != null) job.Worker.Fail(message);
                    else if (job.Process != null && !job.Process.HasExited) job.Process.Kill();
                }
                catch { }
                var status = JsonUtil.Read(job.StatusPath, new JObject()) as JObject ?? new JObject();
                status["state"] = JobStates.Failed;
                status["message"] = message;
                status["adapterError"] = failure.DeepClone();
                status["finishedAt"] = ProviderStore.NowIso();
                RecordProviderTerminal(job, status, false, message);
                JsonUtil.WriteAtomic(job.StatusPath, status);
                job.Error = message;
                job.State = JobStates.Failed;
                job.IsActive = false;
                job.Busy = false;
                job.Runtime.Transition(JobStates.Failed, message, new JObject { ["source"] = "openai-adapter" });
                _eventStore.UpdateRunState(job.Id, JobStates.Failed, job.Process == null ? 0 : job.Process.Id, status.ToString(Formatting.None));
                finalized = true;
            }
            if (!finalized) return;
            OpenAiAdapter.CancelRun(job.Id);
            NativeMetrics.RecordRunEnd(job.Id, JobStates.Failed, job.ElapsedMilliseconds());
            NotifyState();
            if (_window != null && !IsScheduleRun(job)) _window.ShowBackgroundNotification("Claude Code 任务失败", Limit(message, 220));
        }

        private async Task<ModelResult> FetchModels(string baseUrl, string token, string authStyle)
        {
            return await FetchModelsFromUrls(ModelUrls(baseUrl), token, authStyle);
        }

        private async Task<ModelResult> FetchModelsFromUrls(IEnumerable<string> urls, string token, string authStyle)
        {
            var errors = new List<string>();
            authStyle = ((authStyle ?? "auto").Trim().ToLowerInvariant());
            var styles = authStyle == "auto" ? new[] { "bearer", "x-api-key" } : new[] { authStyle };
            var candidates = (urls ?? Enumerable.Empty<string>()).Select(value => (value ?? "").Trim())
                .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("没有填写可用的模型接口地址");
            using (var cancellation = new CancellationTokenSource(ModelDiscoveryTimeout))
            foreach (var url in candidates)
            {
                Uri endpoint;
                if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(SafeProviderDetail(url, token) + " [地址无效]");
                    continue;
                }
                foreach (var style in styles)
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            if (style == "anthropic")
                            {
                                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                                request.Headers.TryAddWithoutValidation("x-api-key", token);
                                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                            }
                            else OpenAiAdapter.ApplyAuth(request, token, style);
                            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
                            {
                                var bodyText = await OpenAiAdapter.ReadContentLimitedAsync(response.Content, MaximumModelListBytes,
                                    ModelDiscoveryTimeout, cancellation.Token, "模型列表响应正文读取超时");
                                if (response.IsSuccessStatusCode)
                                {
                                    var body = JToken.Parse(bodyText);
                                    var values = ModelValues(body);
                                    var capabilityMap = new JObject();
                                    var models = values.Select(value =>
                                    {
                                        if (value is JObject obj)
                                        {
                                            var modelId = (string)obj["id"] ?? (string)obj["name"] ?? (string)obj["model"] ?? (string)obj["model_id"];
                                            if (!string.IsNullOrWhiteSpace(modelId)) capabilityMap[modelId] = ModelCapabilityEvidence(obj, url);
                                            return modelId;
                                        }
                                        return (string)value;
                                    }).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
                                        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                                    if (models.Length > 0) return new ModelResult { Models = models, Url = url, AuthStyle = style, Capabilities = capabilityMap };
                                    errors.Add(SafeProviderDetail(url, token) + " [" + style + "] 接口返回成功，但没有可识别的模型 ID");
                                    continue;
                                }
                                var detail = ModelValidationError(bodyText, token);
                                errors.Add(SafeProviderDetail(url, token) + " [" + style + "] HTTP " + (int)response.StatusCode +
                                    (string.IsNullOrWhiteSpace(detail) ? "" : "：" + detail));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw new InvalidOperationException("模型列表读取超时（" + Math.Ceiling(ModelDiscoveryTimeout.TotalSeconds) + " 秒），已取消仍在等待的网络请求");
                    }
                    catch (TimeoutException error)
                    {
                        throw new InvalidOperationException(SafeProviderDetail(error.Message, token));
                    }
                    catch (Exception error) { errors.Add(SafeProviderDetail(url, token) + " [" + style + "] " + SafeProviderDetail(error.Message, token)); }
                }
            }
            throw new InvalidOperationException(errors.Count == 0 ? "没有找到可用的模型列表接口" : string.Join("；", errors.Skip(Math.Max(0, errors.Count - 4))));
        }

        private static JArray ModelValues(JToken body)
        {
            if (body is JArray direct) return direct;
            var root = body as JObject;
            var data = root?["data"];
            var result = root?["result"];
            var candidates = new[]
            {
                data, root?["models"], (data as JObject)?["models"], (result as JObject)?["models"], (result as JObject)?["data"]
            };
            return candidates.OfType<JArray>().FirstOrDefault() ?? new JArray();
        }

        private static string SafeProviderDetail(string value, string token)
        {
            var safe = value ?? "";
            if (!string.IsNullOrWhiteSpace(token)) safe = safe.Replace(token, "[REDACTED_API_KEY]");
            return Limit(SecretRedactor.Redact(safe), 1600);
        }

        private static JObject ModelCapabilityEvidence(JObject model, string sourceUrl)
        {
            var inputTokens = new List<string>();
            var outputTokens = new List<string>();
            var supportedParameters = new List<string>();
            Action<JToken> collect = token =>
            {
                if (token == null) return;
                if (token is JArray array) inputTokens.AddRange(array.Select(value => ((string)value ?? value.ToString()).ToLowerInvariant()));
                else inputTokens.Add(((string)token ?? token.ToString()).ToLowerInvariant());
            };
            collect(model["input_modalities"]); collect(model["modalities"]); collect(model["architecture"]?["input_modalities"]);
            collect(model["capabilities"]?["input_modalities"]); collect(model["capabilities"]?["modalities"]);
            Action<JToken, List<string>> collectInto = (token, target) =>
            {
                if (token == null) return;
                if (token is JArray array) target.AddRange(array.Select(value => ((string)value ?? value.ToString()).ToLowerInvariant()));
                else target.Add(((string)token ?? token.ToString()).ToLowerInvariant());
            };
            collectInto(model["output_modalities"], outputTokens); collectInto(model["architecture"]?["output_modalities"], outputTokens);
            collectInto(model["capabilities"]?["output_modalities"], outputTokens);
            collectInto(model["supported_parameters"], supportedParameters); collectInto(model["capabilities"]?["supported_parameters"], supportedParameters);

            var explicitVision = FirstBoolean(model, "capabilities.vision", "vision", "supports_vision");
            var hasVisionEvidence = explicitVision != null || inputTokens.Count > 0;
            var vision = explicitVision ?? inputTokens.Any(value => value.Contains("image") || value.Contains("vision"));
            var explicitTools = FirstBoolean(model, "capabilities.tools", "capabilities.tool_use", "supports_tools", "supports_function_calling");
            var hasToolParameterEvidence = supportedParameters.Count > 0;
            var tools = explicitTools ?? supportedParameters.Any(value => value == "tools" || value.Contains("tool_choice") || value.Contains("function"));
            var contextWindow = FirstPositiveModelValue(model, "context_length", "context_window", "max_context_tokens", "max_input_tokens",
                "top_provider.context_length", "architecture.context_length", "capabilities.context_length", "capabilities.context_window");
            var maxOutputTokens = FirstPositiveModelValue(model, "max_output_tokens", "max_completion_tokens", "output_limit",
                "top_provider.max_completion_tokens", "capabilities.max_output_tokens");
            var imageGeneration = outputTokens.Any(value => value.Contains("image"));
            var chat = outputTokens.Count == 0 || outputTokens.Any(value => value.Contains("text") || value.Contains("json"));
            var hasRichEvidence = hasVisionEvidence || explicitTools != null || hasToolParameterEvidence || contextWindow > 0 || maxOutputTokens > 0 || outputTokens.Count > 0;
            return new JObject
            {
                ["chat"] = chat,
                ["vision"] = hasVisionEvidence ? (JToken)new JValue(vision) : JValue.CreateNull(),
                ["tools"] = explicitTools != null || hasToolParameterEvidence ? (JToken)new JValue(tools) : JValue.CreateNull(),
                ["imageGeneration"] = outputTokens.Count > 0 ? (JToken)new JValue(imageGeneration) : new JValue(false),
                ["contextWindow"] = contextWindow > 0 ? (JToken)new JValue(contextWindow) : JValue.CreateNull(),
                ["maxOutputTokens"] = maxOutputTokens > 0 ? (JToken)new JValue(maxOutputTokens) : JValue.CreateNull(),
                ["inputModalities"] = new JArray(inputTokens.Distinct()),
                ["outputModalities"] = new JArray(outputTokens.Distinct()),
                ["evidence"] = hasRichEvidence ? "model-endpoint-metadata" : "model-endpoint-id-only",
                ["evidenceDetails"] = new JObject
                {
                    ["vision"] = hasVisionEvidence ? "model-endpoint-metadata" : "unknown",
                    ["tools"] = explicitTools != null || hasToolParameterEvidence ? "model-endpoint-metadata" : "unknown",
                    ["contextWindow"] = contextWindow > 0 ? "model-endpoint-metadata" : "unknown",
                    ["maxOutputTokens"] = maxOutputTokens > 0 ? "model-endpoint-metadata" : "unknown"
                },
                ["sourceUrl"] = sourceUrl,
                ["checkedAt"] = ProviderStore.NowIso()
            };
        }

        private static bool? FirstBoolean(JObject source, params string[] paths)
        {
            foreach (var path in paths)
            {
                var token = SelectModelToken(source, path);
                if (token == null) continue;
                if (token.Type == JTokenType.Boolean) return (bool)token;
                bool parsed;
                if (bool.TryParse((string)token, out parsed)) return parsed;
            }
            return null;
        }

        private static long FirstPositiveModelValue(JObject source, params string[] paths)
        {
            foreach (var path in paths)
            {
                var token = SelectModelToken(source, path);
                if (token == null) continue;
                long value;
                if ((token.Type == JTokenType.Integer || token.Type == JTokenType.Float) && (value = (long)token) > 0) return value;
                if (long.TryParse((string)token, out value) && value > 0) return value;
            }
            return 0L;
        }

        private static JToken SelectModelToken(JObject source, string path)
        {
            JToken current = source;
            foreach (var segment in (path ?? "").Split('.'))
            {
                current = (current as JObject)?[segment];
                if (current == null) return null;
            }
            return current;
        }

        private static JObject MergeProviderCapabilities(ModelResult textResult, IEnumerable<string> textModels, IEnumerable<string> imageModels, string imageSource)
        {
            var models = textResult == null ? new JObject() : (JObject)(textResult.Capabilities ?? new JObject()).DeepClone();
            foreach (var model in textModels ?? Enumerable.Empty<string>())
                if (models[model] == null) models[model] = new JObject { ["chat"] = true, ["vision"] = JValue.CreateNull(), ["tools"] = JValue.CreateNull(), ["imageGeneration"] = false, ["contextWindow"] = JValue.CreateNull(), ["maxOutputTokens"] = JValue.CreateNull(), ["evidence"] = "model-endpoint-id-only", ["sourceUrl"] = textResult == null ? "" : textResult.Url, ["checkedAt"] = ProviderStore.NowIso() };
            foreach (var model in imageModels ?? Enumerable.Empty<string>())
            {
                var existing = models[model] as JObject;
                if (existing != null)
                {
                    existing["imageGeneration"] = true;
                    existing["imageSourceUrl"] = imageSource ?? "";
                    existing["checkedAt"] = ProviderStore.NowIso();
                    if ((bool?)existing["chat"] == true) existing["evidence"] = "combined-model-endpoint";
                }
                else models[model] = new JObject { ["chat"] = false, ["vision"] = false, ["tools"] = false, ["imageGeneration"] = true, ["contextWindow"] = JValue.CreateNull(), ["maxOutputTokens"] = JValue.CreateNull(), ["evidence"] = "image-model-endpoint", ["sourceUrl"] = imageSource ?? "", ["checkedAt"] = ProviderStore.NowIso() };
            }
            return new JObject { ["schemaVersion"] = 2, ["models"] = models, ["evidencePolicy"] = "endpoint-metadata-not-name-guess" };
        }

        private static IEnumerable<string> ModelUrls(string baseUrl)
        {
            var value = (baseUrl ?? "").Trim().TrimEnd('/');
            var urls = new List<string>();
            if (value.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) urls.Add(value);
            else if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) urls.Add(value + "/models");
            else
            {
                urls.Add(value + "/models"); urls.Add(value + "/v1/models");
                if (value.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
                {
                    var root = value.Substring(0, value.Length - "/anthropic".Length);
                    urls.Add(root + "/models"); urls.Add(root + "/v1/models");
                }
            }
            return urls.Distinct();
        }

        private static string CapabilityEndpoint(string baseUrl, string suffix)
        {
            var value = (baseUrl ?? "").Trim().TrimEnd('/');
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value;
            if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return value + suffix.Substring(3);
            var lastSegment = value.Substring(value.LastIndexOf('/') + 1);
            if (lastSegment.Length > 1 && (lastSegment[0] == 'v' || lastSegment[0] == 'V') && lastSegment.Skip(1).All(char.IsDigit))
                return value + suffix.Substring(3);
            return value + suffix;
        }

        private async Task ServeStatic(HttpListenerResponse response, string relative)
        {
            var clean = relative.Replace('\\', '/').TrimStart('/');
            if (clean.Contains("..")) { await NotFound(response); return; }
            var resource = "static." + clean.Replace('/', '.');
            await ServeResource(response, resource, MimeType(clean), false);
        }

        private async Task ServeResource(HttpListenerResponse response, string resource, string contentType, bool injectSecret)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null) { await NotFound(response); return; }
                using (var memory = new MemoryStream())
                {
                    await stream.CopyToAsync(memory);
                    var bytes = memory.ToArray();
                    if (injectSecret) bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("__DESKTOP_SECRET__", _secret));
                    await WriteBytesAsync(response, bytes, contentType);
                }
            }
        }

        public static async Task<JToken> ReadBody(HttpListenerRequest request)
        {
            if (request == null) return new JObject();
            if (request.ContentLength64 > MaximumRequestBodyBytes)
                throw new RequestBodyTooLargeException("本地请求正文超过安全上限（" + MaximumRequestBodyBytes + " bytes）");
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = request.InputStream.ReadAsync(buffer, 0, buffer.Length);
                    Task completed;
                    using (var delayCancellation = new CancellationTokenSource())
                    {
                        var delay = Task.Delay(RequestBodyIdleTimeout, delayCancellation.Token);
                        completed = await Task.WhenAny(read, delay);
                        if (completed == read) delayCancellation.Cancel();
                    }
                    if (completed != read) throw new RequestBodyTimeoutException("本地请求正文连续 " + Math.Ceiling(RequestBodyIdleTimeout.TotalSeconds) + " 秒没有新数据，连接已终止");
                    var count = await read;
                    if (count <= 0) break;
                    if (memory.Length + count > MaximumRequestBodyBytes)
                        throw new RequestBodyTooLargeException("本地请求正文超过安全上限（" + MaximumRequestBodyBytes + " bytes）");
                    memory.Write(buffer, 0, count);
                }
                var text = new UTF8Encoding(false, true).GetString(memory.ToArray());
                return string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text);
            }
        }

        public static async Task WriteJsonAsync(HttpListenerResponse response, JToken value, int status = 200)
        {
            var bytes = Encoding.UTF8.GetBytes((value ?? JValue.CreateNull()).ToString(Formatting.None));
            response.StatusCode = status;
            await WriteBytesAsync(response, bytes, "application/json; charset=utf-8");
        }

        private static async Task WriteBytesAsync(HttpListenerResponse response, byte[] bytes, string contentType)
        {
            response.ContentType = contentType;
            response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            response.Headers["Pragma"] = "no-cache";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static Task Ok(HttpListenerResponse response) { return WriteJsonAsync(response, new JObject { ["ok"] = true }); }
        private static Task NotFound(HttpListenerResponse response) { return WriteJsonAsync(response, new JObject { ["error"] = "Not found" }, 404); }
        private static string MimeType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".css") return "text/css; charset=utf-8";
            if (ext == ".js") return "application/javascript; charset=utf-8";
            if (ext == ".png") return "image/png";
            if (ext == ".ico") return "image/x-icon";
            if (ext == ".svg") return "image/svg+xml";
            return "application/octet-stream";
        }

        private static string PreviewMimeType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                default: return null;
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string SafeId(string value)
        {
            var result = new string((value ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
            if (result.Length == 0 || result.Length > 100) throw new InvalidOperationException("无效的会话 ID");
            return result;
        }

        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }
        private static string Limit(string value, int max) { return value == null ? "" : value.Length <= max ? value : value.Substring(0, max); }
        private static string Sha256Text(string value)
        {
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(item => item.ToString("X2")));
        }
        private static string ReadSharedText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
        }
        private static string RemoveBearer(string value)
        {
            if (value == null) return "";
            return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value.Substring(7) : value;
        }
        private static string GuessMimeType(string path)
        {
            switch ((Path.GetExtension(path) ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png"; case ".jpg": case ".jpeg": return "image/jpeg"; case ".gif": return "image/gif"; case ".webp": return "image/webp";
                case ".pdf": return "application/pdf"; case ".json": return "application/json"; case ".md": return "text/markdown"; case ".txt": case ".log": return "text/plain";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document"; case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                default: return "application/octet-stream";
            }
        }
        private static bool ConstantEquals(string left, string right)
        {
            var a = Encoding.UTF8.GetBytes(left ?? ""); var b = Encoding.UTF8.GetBytes(right ?? "");
            var diff = a.Length ^ b.Length;
            var length = Math.Max(a.Length, b.Length);
            for (var i = 0; i < length; i++)
            {
                var av = i < a.Length ? a[i] : (byte)0;
                var bv = i < b.Length ? b[i] : (byte)0;
                diff |= av ^ bv;
            }
            return diff == 0;
        }

        private static void StopJob(Job job)
        {
            var wasActive = job.IsActive;
            try { if (job.Cancellation != null) job.Cancellation.Cancel(); } catch { }
            OpenAiAdapter.CancelRun(job.Id);
            if (job.Store != null && !string.IsNullOrWhiteSpace(job.Id))
            {
                try { job.Store.TransitionActiveToolCalls(job.Id, "cancelled", "任务已由用户停止"); } catch { }
            }
            if (job.Worker != null)
            {
                try { job.Worker.Stop(); } catch { }
            }
            if (!string.IsNullOrWhiteSpace(job.PidPath) && File.Exists(job.PidPath))
            {
                try
                {
                    var pid = File.ReadAllText(job.PidPath).Trim();
                    Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit(8000);
                }
                catch { }
            }
            try { if (job.Process != null && !job.Process.HasExited) job.Process.Kill(); } catch { }
            job.IsActive = false;
            job.Busy = false;
            job.State = JobStates.Cancelled;
            if (job.Runtime != null) job.Runtime.Transition(JobStates.Cancelled, "任务已由用户停止");
            if (job.Store != null) job.Store.UpdateRunState(job.Id, JobStates.Cancelled, job.Process == null ? 0 : job.Process.Id, "{}");
            if (string.IsNullOrWhiteSpace(job.Error)) job.Error = "任务已由用户停止";
            if (wasActive) NativeMetrics.RecordRunEnd(job.Id, JobStates.Cancelled, job.ElapsedMilliseconds());
        }

        public void Dispose() { Stop(true); }

        private void NotifyState()
        {
            TouchActivity();
            WriteRuntimeState(IsRunning ? "running" : "stopped");
            var handler = StateChanged;
            if (handler != null) handler(IsRunning);
        }

        private static TaskCompletionSource<long> NewActivitySignal()
        {
            return new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void TouchActivity()
        {
            TaskCompletionSource<long> signal; long revision;
            lock (_activityGate)
            {
                revision = ++_activityRevision;
                signal = _activityChanged;
                _activityChanged = NewActivitySignal();
            }
            try { signal.TrySetResult(revision); } catch { }
        }

        private void WriteRuntimeState(string state)
        {
            try
            {
                JsonUtil.WriteAtomic(Path.Combine(AppPaths.Data, "runtime-state.json"), new JObject
                {
                    ["pid"] = Process.GetCurrentProcess().Id, ["port"] = _port, ["state"] = state,
                    ["protocolVersion"] = ProtocolVersion, ["appVersion"] = Program.AppContractVersion,
                    ["executablePath"] = Assembly.GetExecutingAssembly().Location,
                    ["authProtected"] = SecretStore.Protect(_secret),
                    ["activeJobs"] = _jobs.Values.Count(job => job.IsActive), ["updatedAt"] = ProviderStore.NowIso()
                });
            }
            catch (Exception error) { CrashLog.Handled("RuntimeState", error); }
        }

        private static string LoadOrCreateHostSecret()
        {
            var path = Path.Combine(AppPaths.Data, "host-auth.json");
            foreach (var candidatePath in new[] { path, Path.Combine(AppPaths.Data, "runtime-state.json") })
            {
                try
                {
                    var existing = JsonUtil.Read(candidatePath, new JObject()) as JObject ?? new JObject();
                    var protectedValue = (string)existing["authProtected"] ?? "";
                    if (protectedValue.Length == 0) continue;
                    var value = SecretStore.Unprotect(protectedValue);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (!string.Equals(candidatePath, path, StringComparison.OrdinalIgnoreCase))
                        JsonUtil.WriteAtomic(path, new JObject { ["authProtected"] = SecretStore.Protect(value), ["recoveredAt"] = ProviderStore.NowIso(), ["source"] = "runtime-state" });
                    return value;
                }
                catch { }
            }
            try
            {
                var created = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
                JsonUtil.WriteAtomic(path, new JObject { ["authProtected"] = SecretStore.Protect(created), ["createdAt"] = ProviderStore.NowIso() });
                return created;
            }
            catch
            {
                return Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
            }
        }

        private static TimeSpan ReadTimeoutSetting(string name, int fallbackSeconds)
        {
            int seconds;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out seconds) && seconds >= 1 && seconds <= 600
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(fallbackSeconds);
        }

        private static void InjectBackgroundLoopFailure(string loop, ref int injected)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_MODE"), "1", StringComparison.Ordinal)) return;
            var requested = (Environment.GetEnvironmentVariable("CLAUDE_GUI_TEST_FAIL_BACKGROUND_LOOPS") ?? "")
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (!requested.Any(value => string.Equals(value, loop, StringComparison.OrdinalIgnoreCase))) return;
            if (Interlocked.CompareExchange(ref injected, 1, 0) == 0) throw new IOException("Injected background loop failure: " + loop);
        }

        private static int ReadSizeSetting(string name, int fallbackBytes)
        {
            int bytes;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out bytes) && bytes >= 1024 && bytes <= 256 * 1024 * 1024
                ? bytes
                : fallbackBytes;
        }

        private sealed class RequestBodyTooLargeException : Exception { public RequestBodyTooLargeException(string message) : base(message) { } }
        private sealed class RequestBodyTimeoutException : Exception { public RequestBodyTimeoutException(string message) : base(message) { } }
        private sealed class ModelResult { public string[] Models; public string Url; public string AuthStyle = "bearer"; public JObject Capabilities = new JObject(); }
        private sealed class ProviderPresetDefinition
        {
            public string Id = "";
            public string Name = "";
            public string AuthStyle = "bearer";
            public string ModelAuthStyle = "bearer";
            public string TextProtocol = "openai";
            public string TextBaseUrl = "";
            public string ImageBaseUrl = "";
            public string[] TextModelUrls = new string[0];
            public string[] ImageModelUrls = new string[0];
            public bool SplitCombinedModels;
        }

        private sealed class Job
        {
            private readonly object _readGate = new object();
            public readonly object StateGate = new object();
            private readonly Queue<string> _lineQueue = new Queue<string>();
            private long _offset;
            private string _pending = "";
            public string Id;
            public string Kind;
            public string SessionId;
            public string Workspace;
            public string Model;
            public string State = "running";
            public bool IsActive = true;
            public bool Busy = true;
            public bool Persistent;
            public Process Process;
            public NativeWorkerHandle Worker;
            public CancellationTokenSource Cancellation;
            public string RunDir;
            public string RequestPath;
            public string OutputPath;
            public string ErrorPath;
            public string StatusPath;
            public string PidPath;
            public string InputPath;
            public string InputStatePath;
            public string OutputImage;
            public string Error = "";
            public JToken Usage;
            public bool Recovered;
            public long TurnStartSeq;
            public DateTimeOffset StartedAtUtc;
            public PersistedJobState Runtime;
            public JobEventStore Events;
            public AgentEventStore Store;

            public static Job Chat(string id, string root, AgentEventStore store)
            {
                var job = new Job
                {
                    Id = id, Kind = "chat", RunDir = root,
                    RequestPath = Path.Combine(root, "request.json"), OutputPath = Path.Combine(root, "stream.jsonl"),
                    ErrorPath = Path.Combine(root, "error.txt"), StatusPath = Path.Combine(root, "status.json"), PidPath = Path.Combine(root, "child.pid"),
                    InputPath = Path.Combine(root, "input.jsonl"), InputStatePath = Path.Combine(root, "worker-input-state.json")
                };
                job.Runtime = new PersistedJobState(Path.Combine(root, "job-state.json"), id, "chat");
                job.Events = new JobEventStore(id, job.OutputPath);
                job.Store = store;
                job.TurnStartSeq = job.Runtime.TurnStartSeq;
                try { job.StartedAtUtc = new DateTimeOffset(Directory.GetCreationTimeUtc(root), TimeSpan.Zero); }
                catch { job.StartedAtUtc = DateTimeOffset.UtcNow; }
                return job;
            }
            public long ElapsedMilliseconds() { return Math.Max(0L, (long)(DateTimeOffset.UtcNow - StartedAtUtc.ToUniversalTime()).TotalMilliseconds); }
            public long EventCount() { return Store == null ? Events.Count() : Store.EventCount(Id); }
            public JArray ReadEvents(long after, int max, out long next) { return Store == null ? Events.ReadAfter(after, max, out next) : Store.ReadEvents(Id, after, max, out next); }
            public static Job Image(string id) { return new Job { Id = id, Kind = "image", Cancellation = new CancellationTokenSource(), StartedAtUtc = DateTimeOffset.UtcNow }; }

            public bool HasPendingLines { get { lock (_readGate) { return _lineQueue.Count > 0; } } }

            public void SeekOutputToEnd()
            {
                lock (_readGate)
                {
                    if (File.Exists(OutputPath)) _offset = new FileInfo(OutputPath).Length;
                    _pending = "";
                    _lineQueue.Clear();
                }
            }

            public string[] ReadNewLines(int maxLines)
            {
                lock (_readGate)
                {
                    if (!string.IsNullOrWhiteSpace(OutputPath) && File.Exists(OutputPath))
                    {
                        using (var stream = new FileStream(OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (_offset > stream.Length) { _offset = 0; _pending = ""; _lineQueue.Clear(); }
                            stream.Seek(_offset, SeekOrigin.Begin);
                            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
                            {
                                var text = reader.ReadToEnd();
                                _offset = stream.Position;
                                if (text.Length > 0)
                                {
                                    var combined = _pending + text;
                                    var complete = combined.EndsWith("\n", StringComparison.Ordinal);
                                    var parts = combined.Replace("\r\n", "\n").Split('\n');
                                    _pending = complete ? "" : parts[parts.Length - 1];
                                    var count = complete ? parts.Length : parts.Length - 1;
                                    foreach (var line in parts.Take(count).Where(value => value.Length > 0)) _lineQueue.Enqueue(line);
                                }
                            }
                        }
                    }
                    var result = new List<string>();
                    while (result.Count < Math.Max(1, maxLines) && _lineQueue.Count > 0) result.Add(_lineQueue.Dequeue());
                    return result.ToArray();
                }
            }
        }
    }
}

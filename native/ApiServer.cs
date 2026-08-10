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
        private readonly string _secret = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=');
        private HttpListener _listener;
        private CancellationTokenSource _cancel;
        private Task _acceptLoop;
        private NativeHost _window;
        private int _port;
        private Timer _schedulerTimer;
        private Timer _queueTimer;
        private int _schedulerChecking;
        private int _queueChecking;

        public event Action<bool> StateChanged;
        public bool IsRunning { get; private set; }
        public string BaseUrl { get { return "http://127.0.0.1:" + _port + "/"; } }
        public bool HasActiveJobs { get { return _jobs.Values.Any(job => job.IsActive); } }

        public ApiServer()
        {
            _eventStore = new AgentEventStore();
            _eventStore.InitializeAndMigrate();
            _providers = new ProviderStore(); _workbench = new WorkbenchApi(_eventStore); _permissions = new PermissionBroker(_eventStore);
        }
        public void AttachWindow(NativeHost window) { _window = window; _workbench.AttachWindow(window); _permissions.AttachWindow(window); }

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
                _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancel.Token));
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
                    foreach (var job in _jobs.Values.Where(value => value.IsActive || (value.Persistent && JobProcessAlive(value)))) StopJob(job);
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
                catch (Exception error) { CrashLog.Write("HttpAccept", error); await Task.Delay(100); }
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context)
        {
            context.Response.KeepAlive = false;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try
            {
                var path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath);
                var method = context.Request.HttpMethod.ToUpperInvariant();
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
                if (method == "DELETE" && path.StartsWith("/api/providers/", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(context.Response, new JObject { ["deleted"] = _providers.Delete(path.Substring("/api/providers/".Length)) }); return;
                }
                if (method == "POST" && path == "/api/providers/discover") { await DiscoverModels(context); return; }
                if (method == "POST" && path == "/api/providers/probe") { await ProbeProvider(context); return; }
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
                if (method == "POST" && path == "/api/chat/start") { await StartChat(context); return; }
                if (method == "POST" && path.StartsWith("/api/chat/steer/", StringComparison.Ordinal)) { await SteerChat(context, path.Substring("/api/chat/steer/".Length)); return; }
                if (method == "GET" && path.StartsWith("/api/chat/poll/", StringComparison.Ordinal)) { await PollChat(context, path.Substring("/api/chat/poll/".Length)); return; }
                if (method == "GET" && path.StartsWith("/api/chat/tools/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, _eventStore.ReadToolCalls(SafeId(path.Substring("/api/chat/tools/".Length)))); return; }
                if (method == "GET" && path.StartsWith("/api/chat/context/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, _eventStore.ContextReport(SafeId(path.Substring("/api/chat/context/".Length)))); return; }
                if (method == "GET" && path.StartsWith("/api/chat/artifacts/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, _eventStore.ListArtifacts(SafeId(path.Substring("/api/chat/artifacts/".Length)))); return; }
                if (method == "POST" && path.StartsWith("/api/chat/stop/", StringComparison.Ordinal)) { await StopChat(context.Response, path.Substring("/api/chat/stop/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/chat/pause/", StringComparison.Ordinal)) { await PauseChat(context.Response, path.Substring("/api/chat/pause/".Length)); return; }
                if (method == "POST" && path.StartsWith("/api/chat/resume/", StringComparison.Ordinal)) { await ResumeChat(context.Response, path.Substring("/api/chat/resume/".Length)); return; }
                if (method == "GET" && path == "/api/task-queue") { await ListTaskQueue(context); return; }
                if (method == "POST" && path == "/api/task-queue") { await EnqueueTask(context); return; }
                if (method == "POST" && path.StartsWith("/api/task-queue/cancel/", StringComparison.Ordinal)) { await WriteJsonAsync(context.Response, new JObject { ["cancelled"] = _eventStore.CancelQueue(SafeId(path.Substring("/api/task-queue/cancel/".Length))) }); return; }
                if (method == "POST" && path.StartsWith("/api/task-queue/prioritize/", StringComparison.Ordinal)) { await PrioritizeTaskQueue(context, path.Substring("/api/task-queue/prioritize/".Length)); return; }
                if (method == "POST" && path == "/api/image/start") { await StartImage(context); return; }
                if (method == "GET" && path.StartsWith("/api/image/poll/", StringComparison.Ordinal)) { await PollImage(context.Response, path.Substring("/api/image/poll/".Length)); return; }
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
                CrashLog.Write("HttpRoute", error);
                try { await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 500); } catch { }
            }
            finally
            {
                try { context.Response.OutputStream.Close(); } catch { }
            }
        }

        private async Task Bootstrap(HttpListenerResponse response)
        {
            var fallback = new JObject
            {
                ["providerId"] = "deepseek-default", ["model"] = "deepseek-v4-pro[1m]", ["effort"] = "max", ["permissionMode"] = "agent"
            };
            await WriteJsonAsync(response, new JObject
            {
                ["providers"] = _providers.AllPublic(), ["sessions"] = JsonUtil.Read(AppPaths.SessionsFile, new JArray()),
                ["settings"] = JsonUtil.Read(AppPaths.SettingsFile, fallback), ["workspace"] = AppPaths.Workspace,
                ["version"] = Program.AppContractVersion, ["edition"] = new JObject { ["id"] = EditionInfo.Id, ["productName"] = EditionInfo.ProductName, ["openSource"] = EditionInfo.IsOpenSource }, ["backend"] = "C#/.NET native host", ["trayMode"] = true, ["maxConcurrentWorkers"] = MaxConcurrentWorkers,
                ["persistence"] = _eventStore.Health(),
                ["activeJobs"] = new JArray(_jobs.Values.Where(job => job.IsActive).Select(job => new JObject
                {
                    ["id"] = job.Id, ["kind"] = job.Kind, ["sessionId"] = job.SessionId, ["workspace"] = job.Workspace,
                    ["state"] = job.State, ["recovered"] = job.Recovered, ["eventCursor"] = job.TurnStartSeq,
                    ["jobState"] = job.Runtime == null ? null : job.Runtime.Snapshot
                }))
            });
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
                    if ((state == "completed" || state == "failed" || state == "cancelled") && !childAlive) continue;
                    if (!childAlive)
                    {
                        request["resume"] = ClaudeSessionExists(job.Workspace, (string)request["sessionId"] ?? job.SessionId);
                        var provider = request["provider"] as JObject;
                        if (provider != null && string.Equals((string)provider["protocol"], "openai", StringComparison.OrdinalIgnoreCase))
                            provider["baseUrl"] = BaseUrl.TrimEnd('/') + "/adapter/" + (string)provider["id"];
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
                    job.Busy = state == JobStates.Running || state == JobStates.Starting || state == JobStates.Waiting;
                    job.IsActive = job.Busy;
                    job.State = state;
                    job.Recovered = true;
                    _jobs[id] = job;
                }
                catch (Exception error) { CrashLog.Write("RestoreJob", error); }
            }
        }

        private static bool ProcessAlive(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch { return false; }
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
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "该会话仍有任务运行，无法永久删除" }, 409);
                return;
            }

            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var workspace = Path.GetFullPath(((string)payload["workspace"] ?? AppPaths.Workspace).Trim());
            if (!Directory.Exists(workspace)) workspace = AppPaths.Workspace;
            var transcriptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            foreach (var value in payload["transcriptIds"] as JArray ?? new JArray())
            {
                var candidate = ((string)value ?? "").Trim();
                if (Guid.TryParse(candidate, out _)) transcriptIds.Add(candidate);
            }

            var deletedTranscripts = 0;
            foreach (var transcriptId in transcriptIds)
            {
                var transcript = ClaudeTranscriptPath(workspace, transcriptId);
                if (!File.Exists(transcript)) continue;
                File.Delete(transcript);
                deletedTranscripts++;
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
            try
            {
                var token = ((string)payload["token"] ?? "").Trim();
                if (token.Length == 0 && !string.IsNullOrWhiteSpace((string)payload["providerId"])) token = _providers.Token((string)payload["providerId"]);
                if (token.Length == 0) throw new InvalidOperationException("请先填写或保存 API 令牌");
                var result = await FetchModels((string)payload["baseUrl"] ?? "", token, (string)payload["authStyle"] ?? "auto");
                var imagePattern = new[] { "image", "dall-e", "dalle", "flux", "sdxl", "stable-diffusion", "seedream" };
                var images = result.Models.Where(model => imagePattern.Any(pattern => model.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
                var texts = result.Models.Except(images).ToArray();
                await WriteJsonAsync(context.Response, new JObject
                {
                    ["models"] = new JArray(result.Models), ["textModels"] = new JArray(texts), ["imageModels"] = new JArray(images), ["url"] = result.Url,
                    ["capabilities"] = result.Capabilities ?? new JObject()
                });
            }
            catch (Exception error) { await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 400); }
        }

        private async Task ProbeProvider(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
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
                    imageModels = textResult.Models.Where(IsImageModel).ToArray();
                    textModels = textResult.Models.Where(model => !IsImageModel(model) && !IsNonChatModel(model)).ToArray();
                }
                else textModels = textResult.Models.Where(model => !IsImageModel(model) && !IsNonChatModel(model)).ToArray();

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
            catch (Exception error) { await WriteJsonAsync(context.Response, new JObject { ["error"] = error.Message }, 400); }
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

        private async Task StartChat(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var provider = _providers.Get((string)payload["providerId"] ?? "");
            if (provider == null || !((bool?)provider["text"]?["enabled"] ?? false))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 API 没有启用文字工作能力" }, 400); return;
            }
            var model = ((string)payload["model"] ?? "").Trim();
            if (model.Length == 0) { await WriteJsonAsync(context.Response, new JObject { ["error"] = "请选择文字模型" }, 400); return; }

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
                        durableJob.IsActive = durableJob.State == JobStates.Running || durableJob.State == JobStates.Starting || durableJob.State == JobStates.Waiting;
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
            if (_jobs.Values.Count(value => value.IsActive && value.Kind == "chat") >= MaxConcurrentWorkers)
            {
                context.Response.Headers["Retry-After"] = "2";
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "并行 Agent 已达到上限，任务可进入持久队列等待", ["maxConcurrentWorkers"] = MaxConcurrentWorkers }, 429); return;
            }

            var id = Guid.NewGuid().ToString();
            var sourceWorkspace = workspace;
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
            job.Runtime.Transition(JobStates.Starting, "正在启动 Claude Code Worker");
            _eventStore.UpsertTask(sessionId, workspace, Limit((string)payload["prompt"] ?? "新的 Agent 任务", 80), JobStates.Starting, false, "{}");
            _eventStore.UpsertRun(id, sessionId, requestId, model, workspace, JobStates.Starting, 0);
            File.WriteAllText(job.OutputPath, "", new UTF8Encoding(false));
            File.WriteAllText(job.ErrorPath, "", new UTF8Encoding(false));

            var textConfig = provider["text"] as JObject ?? new JObject();
            var baseUrl = (string)textConfig["baseUrl"] ?? "";
            if ((string)textConfig["protocol"] == "openai") baseUrl = BaseUrl.TrimEnd('/') + "/adapter/" + (string)provider["id"];
            string[] attachments;
            try
            {
                attachments = (payload["attachments"] as JArray ?? new JArray())
                    .Select(value => ((string)value ?? "").Trim()).Where(value => value.Length > 0)
                    .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var path in attachments)
                {
                    if (!File.Exists(path)) throw new InvalidOperationException("附件不存在或已被移动：" + path);
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                }
            }
            catch (Exception error)
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "附件无法读取：" + error.Message }, 400); return;
            }
            var hasVisualAttachment = attachments.Any(path => new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".pdf" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
            var modelCapability = provider["capabilities"]?["models"]?[model] as JObject;
            if (hasVisualAttachment && string.Equals((string)textConfig["protocol"], "openai", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 OpenAI 兼容转换器只传递文字，不能可靠传输图片或 PDF；请切换到已验证的 Anthropic Vision 模型", ["capability"] = "vision", ["evidence"] = "adapter-transport" }, 400); return;
            }
            if (hasVisualAttachment && modelCapability != null && modelCapability["vision"] != null && modelCapability["vision"].Type == JTokenType.Boolean && !(bool)modelCapability["vision"])
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "模型接口元数据明确表示当前模型不支持图片输入", ["capability"] = "vision", ["evidence"] = modelCapability["evidence"] }, 400); return;
            }
            var prompt = (string)payload["prompt"] ?? "";
            if (attachments.Length > 0) prompt += "\n\n用户已明确引用以下本地文件。列表中的每一项都是原始文件的绝对路径，不是上传后的副本；请直接从该路径读取。文本、代码、图片或 PDF 优先使用 Read，Office 等格式请使用可用的 Skill 或本地工具解析。不得假装已经读取；若读取失败，请指出具体文件与原因。\n<attached_files>\n" +
                new JArray(attachments).ToString(Formatting.Indented) + "\n</attached_files>";
            var skillCatalog = SkillCatalog.Build(sourceWorkspace, prompt);
            JsonUtil.WriteAtomic(Path.Combine(runDir, "skill-index.json"), skillCatalog);
            prompt += SkillCatalog.RoutingHint(skillCatalog);
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
            var permissionMode = ((string)payload["permissionMode"] ?? "agent").Trim().ToLowerInvariant();
            if (!new[] { "readonly", "plan", "manual", "scoped", "edit", "agent", "full" }.Contains(permissionMode)) permissionMode = "readonly";
            try
            {
                var isolated = TaskWorkspaceManager.Prepare(sourceWorkspace, id, runDir,
                    permissionMode == "edit" || permissionMode == "agent" || permissionMode == "full", sessionId);
                workspace = isolated.WorkerWorkspace;
                job.Workspace = workspace;
                job.Runtime.SetIdentity(sessionId, workspace, model);
                _eventStore.UpsertRun(id, sessionId, requestId, model, workspace, JobStates.Starting, 0);
            }
            catch (Exception error)
            {
                job.IsActive = false; job.Busy = false; job.State = JobStates.Failed;
                job.Runtime.Transition(JobStates.Failed, error.Message);
                JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = "failed", ["message"] = error.Message });
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "无法建立可回滚的任务工作区：" + error.Message }, 409); return;
            }
            var agentRoots = permissionMode == "agent" || permissionMode == "full" ? AgentRoots() : new string[0];
            var addDirs = attachments.Select(Path.GetDirectoryName).Concat(allowedDirs).Concat(agentRoots)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var allowedTools = permissionMode == "scoped" ? payload["allowedTools"] as JArray ?? new JArray() : new JArray();
            var request = new JObject
            {
                ["workspace"] = workspace, ["sourceWorkspace"] = sourceWorkspace, ["prompt"] = prompt,
                ["guiSessionId"] = sessionId,
                ["sessionId"] = claudeSessionId,
                ["resume"] = ((bool?)payload["resume"] ?? false) && ClaudeSessionExists(workspace, claudeSessionId),
                ["model"] = model, ["effort"] = (string)payload["effort"] ?? "high", ["permissionMode"] = permissionMode,
                ["attachments"] = new JArray(attachments), ["addDirs"] = new JArray(addDirs),
                ["skillIndexPath"] = Path.Combine(runDir, "skill-index.json"), ["matchedSkills"] = skillCatalog["matched"] == null ? new JArray() : skillCatalog["matched"].DeepClone(),
                ["allowedTools"] = allowedTools,
                ["disallowedTools"] = payload["disallowedTools"] as JArray ?? new JArray(),
                ["provider"] = new JObject
                {
                    ["id"] = provider["id"], ["protocol"] = textConfig["protocol"], ["sourceBaseUrl"] = textConfig["baseUrl"],
                    ["baseUrl"] = baseUrl, ["tokenEncrypted"] = provider["tokenEncrypted"]
                }
            };
            var policy = TaskSecurityPolicy.Create(id, permissionMode, workspace, addDirs, allowedTools, request["disallowedTools"] as JArray);
            JsonUtil.WriteAtomic(Path.Combine(runDir, "security-policy.json"), policy);
            _eventStore.RecordContext(id, sessionId, "user", "用户要求", (string)payload["prompt"] ?? "", new JObject());
            _eventStore.RecordContext(id, sessionId, "permission", "任务权限清单", policy.ToString(Formatting.None), new JObject { ["mode"] = permissionMode });
            var routingHint = SkillCatalog.RoutingHint(skillCatalog);
            if (routingHint.Length > 0) _eventStore.RecordContext(id, sessionId, "skill-metadata", "按需命中的 Skill", routingHint, new JObject { ["fullBodyPreloaded"] = false });
            foreach (var attachment in attachments)
            {
                _eventStore.RecordContext(id, sessionId, "attachment-reference", Path.GetFileName(attachment), attachment, new JObject { ["path"] = attachment, ["contentEmbedded"] = false });
                _eventStore.RecordArtifact(sessionId, id, "attachment", attachment, GuessMimeType(attachment), new JObject { ["source"] = "path-reference" });
            }
            var reusableJob = _jobs.Values.FirstOrDefault(value => value.Persistent && value.Kind == "chat" && !value.IsActive &&
                string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) && string.Equals(value.Model, model, StringComparison.Ordinal) && JobProcessAlive(value));
            if (reusableJob != null)
            {
                var reusedOffset = AppendChatInput(reusableJob, prompt);
                await WriteJsonAsync(context.Response, new JObject { ["jobId"] = reusableJob.Id, ["reused"] = true, ["inputOffset"] = reusedOffset, ["eventCursor"] = reusableJob.TurnStartSeq, ["jobState"] = reusableJob.Runtime.Snapshot });
                return;
            }
            ConfigurePermissionBroker(request, runDir, id);
            JsonUtil.WriteAtomic(job.RequestPath, request);
            File.WriteAllText(job.InputPath, "", new UTF8Encoding(false));
            var inputOffset = AppendChatInput(job, prompt);

            _jobs[id] = job;
            try
            {
                job.Worker = NativeWorkerHandle.Start(request, job.OutputPath, job.ErrorPath, job.StatusPath, job.PidPath, job.InputPath, _eventStore, id, sessionId);
                job.Process = job.Worker.Process;
                _eventStore.UpsertRun(id, sessionId, requestId, model, workspace, JobStates.Running, job.Process.Id);
            }
            catch (Exception error)
            {
                job.IsActive = false; job.Busy = false; job.State = "failed";
                job.Runtime.Transition(JobStates.Failed, error.Message);
                JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = "failed", ["message"] = error.Message });
                NotifyState();
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "Claude Code Worker 启动失败：" + error.Message }, 500); return;
            }
            NotifyState();
            await WriteJsonAsync(context.Response, new JObject { ["jobId"] = id, ["inputOffset"] = inputOffset, ["eventCursor"] = job.TurnStartSeq, ["jobState"] = job.Runtime.Snapshot });
        }

        private async Task CheckSchedulesAsync()
        {
            if (!IsRunning || Interlocked.Exchange(ref _schedulerChecking, 1) != 0) return;
            try
            {
                var now = DateTimeOffset.UtcNow;
                var owner = "host:" + Process.GetCurrentProcess().Id;
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
                        ["providerId"] = (string)settings["providerId"] ?? "", ["model"] = (string)settings["model"] ?? "", ["effort"] = (string)settings["effort"] ?? "high",
                        ["permissionMode"] = (string)settings["permissionMode"] ?? "agent", ["allowedTools"] = ToolArray(settings["allowedTools"]), ["disallowedTools"] = ToolArray(settings["disallowedTools"]),
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
                            }
                        }
                        _eventStore.CompleteSchedule(scheduleId, true, "", now);
                        if (session != null) { session["started"] = true; session["updatedAt"] = ProviderStore.NowIso(); JsonUtil.WriteAtomic(AppPaths.SessionsFile, session.Parent); }
                        if (_window != null) _window.ShowNotification("Claude Code 后台调度", "定时任务已由后端启动");
                    }
                    catch (Exception error) { _eventStore.CompleteSchedule(scheduleId, false, error.Message, now); CrashLog.Write("SchedulerRun", error); }
                }
            }
            catch (Exception error) { CrashLog.Write("Scheduler", error); }
            finally { Interlocked.Exchange(ref _schedulerChecking, 0); }
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
            payload["resume"] = true;
            var item = _eventStore.Enqueue(taskId, text, ((string)body["kind"] ?? "queued").Trim(), payload);
            await WriteJsonAsync(context.Response, item);
        }

        private async Task PrioritizeTaskQueue(HttpListenerContext context, string id)
        {
            var body = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var ok = _eventStore.PrioritizeQueue(SafeId(id), (bool?)body["steer"] ?? true);
            await WriteJsonAsync(context.Response, new JObject { ["ok"] = ok });
        }

        private async Task CheckTaskQueueAsync()
        {
            if (!IsRunning || Interlocked.Exchange(ref _queueChecking, 1) != 0) return;
            try
            {
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
                        _eventStore.TransitionQueueAny((string)token["id"], state == "failed" ? "failed" : "completed", runId, expectedOffset);
                        if (sentOffset > 0 && completedOffset >= sentOffset)
                        {
                            running.IsActive = false; running.Busy = false; running.State = state == "failed" ? "failed" : "completed";
                        }
                        continue;
                    }
                    if (state != "failed" && state != "cancelled") continue;
                    _eventStore.TransitionQueueAny((string)token["id"], state, runId, expectedOffset);
                }
                foreach (var token in _eventStore.QueuedItems().OfType<JObject>())
                {
                    if (_jobs.Values.Count(job => job.Kind == "chat" && job.IsActive) >= MaxConcurrentWorkers) break;
                    var queueId = (string)token["id"]; var taskId = (string)token["taskId"];
                    var active = _jobs.Values.FirstOrDefault(job => job.Kind == "chat" && job.IsActive && string.Equals(job.SessionId, taskId, StringComparison.Ordinal));
                    if (active != null)
                    {
                        if ((string)token["kind"] != "steer") continue;
                        if (!_eventStore.TransitionQueue(queueId, "queued", "starting", active.Id)) continue;
                        var steerOffset = AppendChatInput(active, "方向调整：下面是当前最高优先级的新要求。请立即据此调整后续工作方向；已经完成且仍适用的结果可以保留。\n\n" + (string)token["text"]);
                        _eventStore.TransitionQueueAny(queueId, "running", active.Id, steerOffset);
                        continue;
                    }
                    if (!_eventStore.TransitionQueue(queueId, "queued", "starting")) continue;
                    var payload = token["payload"] as JObject ?? new JObject();
                    var model = (string)payload["model"] ?? "";
                    var reusable = _jobs.Values.FirstOrDefault(job => job.Kind == "chat" && !job.IsActive && JobProcessAlive(job) &&
                        string.Equals(job.SessionId, taskId, StringComparison.Ordinal) && (model.Length == 0 || string.Equals(job.Model, model, StringComparison.Ordinal)));
                    if (reusable != null)
                    {
                        var reusedOffset = AppendChatInput(reusable, (string)token["text"]);
                        _eventStore.TransitionQueueAny(queueId, "running", reusable.Id, reusedOffset);
                        continue;
                    }
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
                                _eventStore.TransitionQueueAny(queueId, "running", (string)result["jobId"] ?? "", (long?)result["inputOffset"] ?? 0L);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        CrashLog.Write("TaskQueue", error); _eventStore.TransitionQueueAny(queueId, "failed");
                    }
                }
            }
            finally { Interlocked.Exchange(ref _queueChecking, 0); }
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

        private long AppendChatInput(Job job, string text)
        {
            job.TurnStartSeq = job.EventCount();
            var input = new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text ?? "" }) } };
            File.AppendAllText(job.InputPath, input.ToString(Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
            JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = "running", ["message"] = "Claude Code 正在处理输入", ["startedAt"] = ProviderStore.NowIso() });
            job.IsActive = true; job.Busy = true; job.State = "running";
            job.Runtime.BeginTurn(job.TurnStartSeq, "Claude Code 正在处理输入");
            NotifyState();
            return new FileInfo(job.InputPath).Length;
        }

        private async Task PollChat(HttpListenerContext context, string id)
        {
            var response = context.Response;
            Job job;
            if (!_jobs.TryGetValue(id, out job)) { await WriteJsonAsync(response, new JObject { ["error"] = "任务不存在" }, 404); return; }
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
            if ((state == "completed" || state == "failed") && nextSeq < job.EventCount())
            {
                status["terminalState"] = state;
                status["state"] = "running";
                status["message"] = "正在整理剩余流式输出";
                state = "running";
            }
            job.State = state;
            if (state == "failed")
            {
                error = File.Exists(job.ErrorPath) ? ReadSharedText(job.ErrorPath) : "";
                if (string.IsNullOrWhiteSpace(error)) error = (string)status["message"] ?? "请求失败，但上游未返回错误详情";
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
            var wasActive = job.IsActive;
            if (state == "completed" || state == "failed")
            {
                job.IsActive = false; job.Busy = false;
                job.Runtime.Transition(state == "completed" ? JobStates.Completed : JobStates.Failed, (string)status["message"] ?? error,
                    new JObject { ["eventSeq"] = nextSeq, ["exitCode"] = status["exitCode"] });
                _eventStore.UpdateRunState(job.Id, state, job.Process == null ? 0 : job.Process.Id, status.ToString(Formatting.None));
            }
            if (wasActive && !job.IsActive) NotifyState();
            await WriteJsonAsync(response, new JObject { ["events"] = events, ["lines"] = lines, ["nextSeq"] = nextSeq, ["status"] = status, ["jobState"] = job.Runtime.Snapshot, ["error"] = error });
        }

        private JObject BuildProviderFallbackDecision(Job job, string failure)
        {
            try
            {
                var request = JsonUtil.Read(job.RequestPath, new JObject()) as JObject ?? new JObject();
                var currentProviderId = (string)request["provider"]?["id"] ?? "";
                var candidates = new JArray();
                foreach (var provider in _providers.AllPublic().OfType<JObject>())
                {
                    var id = (string)provider["id"] ?? "";
                    var text = provider["text"] as JObject;
                    var models = text?["models"] as JArray ?? new JArray();
                    if (id.Length == 0 || string.Equals(id, currentProviderId, StringComparison.OrdinalIgnoreCase) ||
                        !((bool?)text?["enabled"] ?? false) || models.Count == 0) continue;
                    candidates.Add(new JObject
                    {
                        ["providerId"] = id,
                        ["name"] = (string)provider["name"] ?? id,
                        ["protocol"] = (string)text["protocol"] ?? "unknown",
                        ["models"] = new JArray(models.Take(6).Select(value => (string)value).Where(value => !string.IsNullOrWhiteSpace(value)))
                    });
                }
                if (candidates.Count == 0) return null;
                return new JObject
                {
                    ["strategy"] = "user-confirmed",
                    ["automatic"] = false,
                    ["currentProviderId"] = currentProviderId,
                    ["reason"] = Limit(failure, 600),
                    ["candidates"] = candidates,
                    ["createdAt"] = ProviderStore.NowIso()
                };
            }
            catch (Exception error) { CrashLog.Write("ProviderFallback", error); return null; }
        }

        private static bool ClaudeSessionExists(string workspace, string sessionId)
        {
            try { return File.Exists(ClaudeTranscriptPath(workspace, sessionId)); }
            catch { return false; }
        }

        private static string ClaudeTranscriptPath(string workspace, string sessionId)
        {
            var fullWorkspace = Path.GetFullPath(workspace ?? "");
            var projectKey = new string(fullWorkspace.Select(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '-').ToArray());
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects", projectKey, sessionId + ".jsonl");
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
            var mode = ((string)request["permissionMode"] ?? "readonly").ToLowerInvariant();
            if (!new[] { "manual", "scoped", "edit", "agent" }.Contains(mode))
            {
                request.Remove("permissionMcpConfig");
                return;
            }
            var permissionConfig = Path.Combine(runDir, "permissions.mcp.json");
            JsonUtil.WriteAtomic(permissionConfig, new JObject { ["mcpServers"] = new JObject { ["gui_permissions"] = new JObject
            {
                ["command"] = Assembly.GetExecutingAssembly().Location,
                ["args"] = new JArray("--permission-mcp"),
                ["env"] = new JObject { ["CLAUDE_GUI_PERMISSION_BASE"] = BaseUrl, ["CLAUDE_GUI_PERMISSION_SECRET_PROTECTED"] = SecretStore.Protect(_secret), ["CLAUDE_GUI_PERMISSION_JOB"] = jobId }
            } } });
            request["permissionMcpConfig"] = permissionConfig;
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
            await WriteJsonAsync(response, new JObject { ["paused"] = true, ["jobId"] = id });
        }

        private async Task ResumeChat(HttpListenerResponse response, string id)
        {
            Job job;
            if (!_jobs.TryGetValue(id, out job) || job.Worker == null || !job.Worker.Resume()) { await WriteJsonAsync(response, new JObject { ["resumed"] = false, ["error"] = "当前任务无法继续" }, 409); return; }
            job.State = JobStates.Running; job.Runtime.Transition(JobStates.Running, "任务已继续");
            JsonUtil.WriteAtomic(job.StatusPath, new JObject { ["state"] = JobStates.Running, ["message"] = "任务已继续", ["resumedAt"] = ProviderStore.NowIso() });
            await WriteJsonAsync(response, new JObject { ["resumed"] = true, ["jobId"] = id });
        }

        private async Task StartImage(HttpListenerContext context)
        {
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            var provider = _providers.Get((string)payload["providerId"] ?? "");
            if (provider == null || !((bool?)provider["image"]?["enabled"] ?? false))
            {
                await WriteJsonAsync(context.Response, new JObject { ["error"] = "当前 API 没有启用图像生成能力" }, 400); return;
            }
            var id = Guid.NewGuid().ToString();
            var job = Job.Image(id);
            job.SessionId = ((string)payload["sessionId"] ?? id).Trim(); if (job.SessionId.Length == 0) job.SessionId = id;
            job.Workspace = ((string)payload["workspace"] ?? AppPaths.Workspace).Trim();
            _jobs[id] = job;
            _eventStore.UpsertTask(job.SessionId, job.Workspace, Limit((string)payload["prompt"] ?? "生成图片", 80), JobStates.Running, false, "{}");
            _eventStore.UpsertRun(id, job.SessionId, "image:" + id, (string)payload["model"] ?? "", job.Workspace, JobStates.Running, 0);
            NotifyState();
            var imageTask = Task.Run(() => RunImageAsync(job, provider, (string)payload["model"] ?? "", (string)payload["prompt"] ?? "", (string)payload["size"] ?? "1024x1024"));
            await WriteJsonAsync(context.Response, new JObject { ["jobId"] = id });
        }

        private async Task RunImageAsync(Job job, JObject provider, string model, string prompt, string size)
        {
            try
            {
                var token = _providers.Token((string)provider["id"]);
                var config = provider["image"] as JObject ?? new JObject();
                var url = CapabilityEndpoint((string)config["baseUrl"], "/v1/images/generations");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    OpenAiAdapter.ApplyAuth(request, token, (string)provider["authStyle"] ?? "auto");
                    request.Content = new StringContent(new JObject
                    {
                        ["model"] = model, ["prompt"] = prompt, ["size"] = size, ["n"] = 1, ["response_format"] = "b64_json"
                    }.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (var result = await Http.SendAsync(request))
                    {
                        var text = await result.Content.ReadAsStringAsync();
                        if (!result.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)result.StatusCode + ": " + Limit(text, 1500));
                        var body = JObject.Parse(text);
                        var item = body["data"]?[0] as JObject ?? new JObject();
                        var output = Path.Combine(AppPaths.Images, "生成图片-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                        var base64 = (string)item["b64_json"];
                        if (!string.IsNullOrWhiteSpace(base64)) File.WriteAllBytes(output, Convert.FromBase64String(base64));
                        else if (!string.IsNullOrWhiteSpace((string)item["url"])) File.WriteAllBytes(output, await Http.GetByteArrayAsync((string)item["url"]));
                        else throw new InvalidOperationException("图像接口没有返回 b64_json 或 url");
                        job.OutputImage = output;
                        job.Usage = body["usage"];
                        _eventStore.RecordArtifact(job.SessionId, job.Id, "generated-image", output, "image/png", new JObject { ["model"] = model, ["size"] = size, ["providerId"] = provider["id"] });
                        _eventStore.UpdateRunState(job.Id, JobStates.Completed, 0, new JObject { ["outputPath"] = output }.ToString(Formatting.None));
                        job.State = "completed";
                        job.IsActive = false;
                        NotifyState();
                    }
                }
            }
            catch (Exception error) { job.Error = error.Message; job.State = "failed"; job.IsActive = false; _eventStore.UpdateRunState(job.Id, JobStates.Failed, 0, new JObject { ["error"] = error.Message }.ToString(Formatting.None)); NotifyState(); }
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
            await WriteBytesAsync(response, File.ReadAllBytes(job.OutputImage), "image/png");
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
            var payload = JsonUtil.ObjectOrEmpty(await ReadBody(context.Request));
            if (tail == "/v1/messages/count_tokens") await WriteJsonAsync(context.Response, OpenAiAdapter.CountTokens(payload));
            else if (tail == "/v1/messages") await OpenAiAdapter.HandleAsync(context, provider, token, payload);
            else await NotFound(context.Response);
        }

        private async Task<ModelResult> FetchModels(string baseUrl, string token, string authStyle)
        {
            return await FetchModelsFromUrls(ModelUrls(baseUrl), token, authStyle);
        }

        private async Task<ModelResult> FetchModelsFromUrls(IEnumerable<string> urls, string token, string authStyle)
        {
            var errors = new List<string>();
            var styles = authStyle == "auto" ? new[] { "bearer", "x-api-key" } : new[] { authStyle };
            foreach (var url in urls)
            {
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
                            using (var response = await Http.SendAsync(request))
                            {
                                var bodyText = await response.Content.ReadAsStringAsync();
                                if (response.IsSuccessStatusCode)
                                {
                                    var body = JToken.Parse(bodyText);
                                    var values = body["data"] ?? body["models"] ?? body;
                                    var capabilityMap = new JObject();
                                    var models = (values as JArray ?? new JArray()).Select(value =>
                                    {
                                        if (value is JObject obj)
                                        {
                                            var modelId = (string)obj["id"] ?? (string)obj["name"];
                                            if (!string.IsNullOrWhiteSpace(modelId)) capabilityMap[modelId] = ModelCapabilityEvidence(obj, url);
                                            return modelId;
                                        }
                                        return (string)value;
                                    }).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().OrderBy(value => value).ToArray();
                                    if (models.Length > 0) return new ModelResult { Models = models, Url = url, Capabilities = capabilityMap };
                                }
                                errors.Add(url + " [" + style + "] HTTP " + (int)response.StatusCode);
                            }
                        }
                    }
                    catch (Exception error) { errors.Add(url + " [" + style + "] " + error.Message); }
                }
            }
            throw new InvalidOperationException(errors.Count == 0 ? "没有找到可用的模型列表接口" : string.Join("；", errors.Skip(Math.Max(0, errors.Count - 4))));
        }

        private static JObject ModelCapabilityEvidence(JObject model, string sourceUrl)
        {
            var tokens = new List<string>();
            Action<JToken> collect = token =>
            {
                if (token == null) return;
                if (token is JArray array) tokens.AddRange(array.Select(value => ((string)value ?? value.ToString()).ToLowerInvariant()));
                else tokens.Add(((string)token ?? token.ToString()).ToLowerInvariant());
            };
            collect(model["input_modalities"]); collect(model["modalities"]); collect(model["architecture"]?["input_modalities"]);
            collect(model["capabilities"]?["input_modalities"]); collect(model["capabilities"]?["modalities"]);
            var hasModalityEvidence = tokens.Count > 0 || model["capabilities"]?["vision"] != null;
            var vision = (bool?)model["capabilities"]?["vision"] ?? tokens.Any(value => value.Contains("image") || value.Contains("vision"));
            return new JObject
            {
                ["chat"] = true, ["vision"] = hasModalityEvidence ? (JToken)new JValue(vision) : JValue.CreateNull(),
                ["imageGeneration"] = false, ["evidence"] = hasModalityEvidence ? "model-endpoint-metadata" : "model-endpoint-id-only",
                ["sourceUrl"] = sourceUrl, ["checkedAt"] = ProviderStore.NowIso()
            };
        }

        private static JObject MergeProviderCapabilities(ModelResult textResult, IEnumerable<string> textModels, IEnumerable<string> imageModels, string imageSource)
        {
            var models = textResult == null ? new JObject() : (JObject)(textResult.Capabilities ?? new JObject()).DeepClone();
            foreach (var model in textModels ?? Enumerable.Empty<string>())
                if (models[model] == null) models[model] = new JObject { ["chat"] = true, ["vision"] = JValue.CreateNull(), ["imageGeneration"] = false, ["evidence"] = "model-endpoint-id-only", ["sourceUrl"] = textResult == null ? "" : textResult.Url, ["checkedAt"] = ProviderStore.NowIso() };
            foreach (var model in imageModels ?? Enumerable.Empty<string>())
                models[model] = new JObject { ["chat"] = false, ["vision"] = false, ["imageGeneration"] = true, ["evidence"] = "image-model-endpoint", ["sourceUrl"] = imageSource ?? "", ["checkedAt"] = ProviderStore.NowIso() };
            return new JObject { ["schemaVersion"] = 1, ["models"] = models, ["evidencePolicy"] = "endpoint-metadata-not-name-guess" };
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
            using (var reader = new StreamReader(request.InputStream, new UTF8Encoding(false, true), true))
            {
                var text = await reader.ReadToEndAsync();
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
        }

        public void Dispose() { Stop(true); }

        private void NotifyState()
        {
            WriteRuntimeState(IsRunning ? "running" : "stopped");
            var handler = StateChanged;
            if (handler != null) handler(IsRunning);
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
            catch (Exception error) { CrashLog.Write("RuntimeState", error); }
        }

        private sealed class ModelResult { public string[] Models; public string Url; public JObject Capabilities = new JObject(); }
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
                return job;
            }
            public long EventCount() { return Store == null ? Events.Count() : Store.EventCount(Id); }
            public JArray ReadEvents(long after, int max, out long next) { return Store == null ? Events.ReadAfter(after, max, out next) : Store.ReadEvents(Id, after, max, out next); }
            public static Job Image(string id) { return new Job { Id = id, Kind = "image" }; }

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

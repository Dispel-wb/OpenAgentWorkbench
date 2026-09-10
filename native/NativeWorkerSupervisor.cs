using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class NativeWorkerHandle : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly JObject _request;
        private readonly string _outputPath;
        private readonly string _errorPath;
        private readonly string _statusPath;
        private readonly string _pidPath;
        private readonly string _inputPath;
        private readonly string _heartbeatPath;
        private readonly string _inputStatePath;
        private readonly AgentEventStore _eventStore;
        private readonly string _runId;
        private readonly string _taskId;
        private readonly JObject _toolRuntimeManifest;
        private readonly ToolRuntimeSettings _toolRuntime;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly object _statusGate = new object();
        private readonly object _inputGate = new object();
        private readonly Queue<long> _pendingInputOffsets = new Queue<long>();
        private NativeJobObject _jobObject;
        private Task _outputTask;
        private Task _errorTask;
        private Task _inputTask;
        private Task _heartbeatTask;
        private string _streamError = "";
        private string _stderr = "";
        private string _workerHarness = "native";
        private bool _terminalSeen;
        private bool _terminalFailed;
        private bool _disposed;
        private bool _stopped;
        private volatile bool _paused;
        private long _lastSentOffset;
        private long _completedInputOffset;
        private long _responseLineOrdinal;
        private long _activeResponseOffset = -1;

        public Process Process { get; private set; }
        public int ProcessId { get { try { return Process == null ? 0 : Process.Id; } catch { return 0; } } }
        public bool IsAlive { get { try { return Process != null && !Process.HasExited; } catch { return false; } } }

        private NativeWorkerHandle(JObject request, string outputPath, string errorPath, string statusPath, string pidPath, string inputPath, AgentEventStore eventStore, string runId, string taskId)
        {
            _request = request;
            _outputPath = outputPath;
            _errorPath = errorPath;
            _statusPath = statusPath;
            _pidPath = pidPath;
            _inputPath = inputPath;
            _eventStore = eventStore;
            _runId = runId ?? "";
            _taskId = taskId ?? "";
            _toolRuntimeManifest = request["toolRuntimePolicy"] as JObject ?? ToolRuntimePolicy.DefaultManifest();
            _toolRuntime = ToolRuntimeSettings.From(_toolRuntimeManifest);
            _heartbeatPath = Path.Combine(Path.GetDirectoryName(statusPath), "worker-heartbeat.json");
            _inputStatePath = Path.Combine(Path.GetDirectoryName(statusPath), "worker-input-state.json");
            var inputState = JsonUtil.Read(_inputStatePath, new JObject()) as JObject ?? new JObject();
            _completedInputOffset = Math.Max(0L, (long?)inputState["completedOffset"] ?? 0L);
            _lastSentOffset = _completedInputOffset;
        }

        public static NativeWorkerHandle Start(JObject request, string outputPath, string errorPath, string statusPath, string pidPath, string inputPath, AgentEventStore eventStore = null, string runId = null, string taskId = null)
        {
            var handle = new NativeWorkerHandle((JObject)request.DeepClone(), outputPath, errorPath, statusPath, pidPath, inputPath, eventStore, runId, taskId);
            handle.StartCore();
            return handle;
        }

        private void StartCore()
        {
            if (_eventStore != null && _runId.Length > 0)
                _eventStore.TransitionActiveToolCalls(_runId, "failed", "Worker restarted before the Tool returned a result.");
            var launch = AgentWorkerSdk.Resolve(_request, Path.GetDirectoryName(_statusPath));
            _workerHarness = launch.Harness;
            var startInfo = new ProcessStartInfo
            {
                FileName = launch.Executable,
                Arguments = string.Join(" ", launch.Arguments.Select(QuoteArgument)),
                WorkingDirectory = (string)_request["workspace"] ?? AppPaths.Workspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8
            };
            if (launch.Harness == "claude") ApplyEnvironment(startInfo, _request);
            AgentWorkerSdk.ApplyProviderEnvironment(startInfo, _request, launch.Harness);
            Process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            Process.Exited += OnExited;
            if (!Process.Start()) throw new InvalidOperationException("Failed to start Claude Code native worker.");
            _jobObject = NativeJobObject.Attach(Process);
            File.WriteAllText(_pidPath, Process.Id.ToString(), new UTF8Encoding(false));
            WriteHeartbeat("starting");
            _outputTask = Task.Run(() => PumpOutputAsync(_cancel.Token));
            _errorTask = Task.Run(() => PumpErrorAsync(_cancel.Token));
            _inputTask = Task.Run(() => PumpInputAsync(_cancel.Token));
            _heartbeatTask = Task.Run(() => PumpHeartbeatAsync(_cancel.Token));
        }

        internal static string FindClaudeExecutable()
        {
            return ClaudeExecutableCandidates().Select(item => item.Value).FirstOrDefault(IsUsableClaudeExecutable) ?? "";
        }

        internal static JObject ClaudeRuntimeDiagnostics(bool probe)
        {
            var candidates = ClaudeExecutableCandidates().ToList();
            var selected = candidates.FirstOrDefault(item => IsUsableClaudeExecutable(item.Value));
            var result = new JObject
            {
                ["available"] = !string.IsNullOrWhiteSpace(selected.Value),
                ["path"] = selected.Value ?? "",
                ["source"] = selected.Key ?? "",
                ["configuredPath"] = ConfiguredClaudeExecutable(),
                ["candidateCount"] = candidates.Count,
                ["checked"] = new JArray(candidates.Select(item => new JObject
                {
                    ["source"] = item.Key, ["path"] = item.Value, ["exists"] = IsUsableClaudeExecutable(item.Value)
                }))
            };
            if (!probe || string.IsNullOrWhiteSpace(selected.Value)) return result;
            var timer = Stopwatch.StartNew();
            try
            {
                var info = new ProcessStartInfo(selected.Value, "--version")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
                };
                using (var process = Process.Start(info))
                {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(8000))
                    {
                        try { process.Kill(); } catch { }
                        result["probeOk"] = false; result["probeError"] = "Claude Code --version 探测超时";
                    }
                    else
                    {
                        Task.WaitAll(new Task[] { output, error }, 2000);
                        result["probeOk"] = process.ExitCode == 0;
                        result["version"] = LimitRuntimeText((output.Result ?? "").Trim(), 300);
                        result["probeError"] = LimitRuntimeText((error.Result ?? "").Trim(), 1000);
                        result["exitCode"] = process.ExitCode;
                    }
                }
            }
            catch (Exception error) { result["probeOk"] = false; result["probeError"] = LimitRuntimeText(error.Message, 1000); }
            result["probeLatencyMs"] = timer.ElapsedMilliseconds;
            return result;
        }

        private static IEnumerable<KeyValuePair<string, string>> ClaudeExecutableCandidates()
        {
            var result = new List<KeyValuePair<string, string>>();
            AddClaudeCandidate(result, "settings", ConfiguredClaudeExecutable());
            AddClaudeCandidate(result, "environment", Environment.GetEnvironmentVariable("CLAUDE_GUI_CLAUDE_EXE"));
            AddClaudeRoots(result, "install", AppPaths.ClaudeRoot);
            AddClaudeRoots(result, "application", AppContext.BaseDirectory);
            AddClaudeRoots(result, "user-npm", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            AddClaudeRoots(result, "local-npm", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm"));
            AddClaudeRoots(result, "nodejs", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                AddClaudeRoots(result, "path", directory == null ? "" : directory.Trim().Trim('"'));
            return result.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        }

        private static string ConfiguredClaudeExecutable()
        {
            try
            {
                var settings = JsonUtil.Read(AppPaths.SettingsFile, new JObject()) as JObject ?? new JObject();
                return ((string)settings["claudeExecutable"] ?? "").Trim().Trim('"');
            }
            catch { return ""; }
        }

        private static void AddClaudeRoots(ICollection<KeyValuePair<string, string>> result, string source, string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            root = root.Trim().Trim('"');
            AddClaudeCandidate(result, source, Path.Combine(root, "claude.exe"));
            AddClaudeCandidate(result, source, Path.Combine(root, "bin", "claude.exe"));
            var package = Path.Combine(root, "node_modules", "@anthropic-ai", "claude-code");
            AddClaudeCandidate(result, source, Path.Combine(package, "bin", "claude.exe"));
            AddClaudeCandidate(result, source, Path.Combine(package, "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe"));
            AddClaudeCandidate(result, source, Path.Combine(package, "node_modules", "@anthropic-ai", "claude-code-win32-arm64", "claude.exe"));
        }

        private static void AddClaudeCandidate(ICollection<KeyValuePair<string, string>> result, string source, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { result.Add(new KeyValuePair<string, string>(source, Path.GetFullPath(path.Trim().Trim('"')))); }
            catch { }
        }

        private static bool IsUsableClaudeExecutable(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }

        private static string LimitRuntimeText(string value, int limit)
        {
            value = value ?? ""; return value.Length <= limit ? value : value.Substring(0, limit);
        }

        internal static List<string> BuildClaudeArguments(JObject request)
        {
            var maxTurns = Math.Max(10, Math.Min(500, (int?)request["maxTurns"] ?? 100));
            var args = new List<string>
            {
                "-p", "--bare", "--input-format", "stream-json", "--output-format", "stream-json",
                "--include-partial-messages", "--include-hook-events", "--forward-subagent-text", "--replay-user-messages",
                "--autocompact", "auto", "--verbose", "--max-turns", maxTurns.ToString(),
                "--model", (string)request["model"] ?? "", "--effort", (string)request["effort"] ?? "high"
            };
            if ((bool?)request["resume"] ?? false) { args.Add("--resume"); args.Add((string)request["sessionId"] ?? ""); }
            else { args.Add("--session-id"); args.Add((string)request["sessionId"] ?? ""); }
            var permissionMode = ((string)request["permissionMode"] ?? "readonly").ToLowerInvariant();
            var permissionConfig = (string)request["permissionMcpConfig"] ?? "";
            var mcpConfigs = (request["mcpConfigs"] as JArray ?? new JArray()).Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (mcpConfigs.Length == 0 && permissionConfig.Length > 0) mcpConfigs = new[] { permissionConfig };
            var brokerEnabled = (bool?)request["permissionBrokerEnabled"] ?? false;
            if (brokerEnabled)
            {
                args.Add("--permission-mode"); args.Add("manual");
                args.Add("--allowedTools"); args.Add("mcp__gui_permissions__approval_prompt");
                args.Add("--permission-prompt-tool"); args.Add("mcp__gui_permissions__approval_prompt");
                if (permissionMode == "scoped")
                {
                    // Tool availability is not permission to use that tool on every path.
                    // --allowed-tools bypasses the broker; use --tools to expose the list,
                    // and let the durable manifest decide each file/command request.
                    args.Add("--tools");
                    args.Add(string.Join(",", (request["allowedTools"] as JArray ?? new JArray()).Values<string>()));
                }
            }
            else if (permissionMode == "plan") { args.Add("--permission-mode"); args.Add("plan"); }
            else if (permissionMode == "full") args.Add("--dangerously-skip-permissions");
            else
            {
                args.Add("--permission-mode"); args.Add("dontAsk");
                if (permissionMode != "scoped") { args.Add("--tools"); args.Add("Read,Glob,Grep,WebSearch,WebFetch,Skill"); }
            }
            if (mcpConfigs.Length > 0)
            {
                args.Add("--strict-mcp-config"); args.Add("--mcp-config");
                foreach (var config in mcpConfigs) args.Add(config);
            }
            var trustedInstructions = (string)request["trustedInstructionPath"] ?? "";
            if (trustedInstructions.Length > 0) { args.Add("--append-system-prompt-file"); args.Add(trustedInstructions); }
            var trustedAgents = request["trustedAgents"] as JObject;
            if (trustedAgents != null && trustedAgents.Count > 0) { args.Add("--agents"); args.Add(trustedAgents.ToString(Newtonsoft.Json.Formatting.None)); }
            if (!brokerEnabled) AddRepeated(args, "--allowed-tools", request["allowedTools"] as JArray);
            AddRepeated(args, "--disallowed-tools", request["disallowedTools"] as JArray);
            AddRepeated(args, "--add-dir", request["addDirs"] as JArray);
            return args;
        }

        private static void AddRepeated(List<string> args, string name, JArray values)
        {
            var items = (values ?? new JArray()).Select(value => ((string)value ?? "").Trim()).Where(value => value.Length > 0).ToArray();
            if (items.Length == 0) return;
            if (name == "--add-dir") foreach (var value in items) { args.Add(name); args.Add(value); }
            else { args.Add(name); foreach (var value in items) args.Add(value); }
        }

        internal static string QuoteArgument(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
            var output = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { output.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                output.Append('\\', slashes).Append(character); slashes = 0;
            }
            output.Append('\\', slashes * 2).Append('"');
            return output.ToString();
        }

        private static void ApplyEnvironment(ProcessStartInfo startInfo, JObject request)
        {
            var names = new[]
            {
                "CLAUDE_CODE_GIT_BASH_PATH", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_MODEL",
                "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL",
                "CLAUDE_CODE_SUBAGENT_MODEL", "CLAUDE_CODE_EFFORT_LEVEL"
            };
            foreach (var name in names)
            {
                var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(value)) startInfo.EnvironmentVariables[name] = value;
            }
            var provider = request["provider"] as JObject;
            if (provider != null && !string.IsNullOrWhiteSpace((string)provider["tokenEncrypted"]))
            {
                var providerToken = SecretStore.Unprotect((string)provider["tokenEncrypted"]);
                var authStyle = ((string)provider["authStyle"] ?? "auto").Trim().ToLowerInvariant();
                if (authStyle == "x-api-key")
                {
                    startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = providerToken;
                    startInfo.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = "";
                }
                else
                {
                    startInfo.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = providerToken;
                    startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = "";
                }
                if (!string.IsNullOrWhiteSpace((string)provider["baseUrl"])) startInfo.EnvironmentVariables["ANTHROPIC_BASE_URL"] = (string)provider["baseUrl"];
            }
            var selectedModel = ((string)request["model"] ?? "").Trim();
            if (selectedModel.Length > 0)
            {
                foreach (var name in new[]
                {
                    "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
                    "ANTHROPIC_DEFAULT_HAIKU_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL"
                }) startInfo.EnvironmentVariables[name] = selectedModel;
            }
            startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
            // The workbench owns conversation titles. A CLI title request shares the Run's
            // adapter URL and can otherwise charge tokens or fail an unrelated active turn.
            startInfo.EnvironmentVariables["CLAUDE_CODE_DISABLE_TERMINAL_TITLE"] = "1";
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            startInfo.EnvironmentVariables["LANG"] = "zh_CN.UTF-8";
        }

        private async Task PumpInputAsync(CancellationToken token)
        {
            long offset = _completedInputOffset;
            var pending = new MemoryStream();
            var buffer = new byte[8192];
            while (!token.IsCancellationRequested && IsAlive)
            {
                try
                {
                    if (_paused) { await Task.Delay(80, token); continue; }
                    if (File.Exists(_inputPath))
                    {
                        using (var stream = new FileStream(_inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (offset > stream.Length) { offset = 0; pending.SetLength(0); }
                            stream.Position = offset;
                            int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            {
                                var chunkStart = offset;
                                offset += count;
                                for (var index = 0; index < count; index++)
                                {
                                    var current = buffer[index];
                                    if (current != (byte)'\n') { pending.WriteByte(current); continue; }
                                    var lineBytes = pending.ToArray(); pending.SetLength(0);
                                    var length = lineBytes.Length > 0 && lineBytes[lineBytes.Length - 1] == (byte)'\r' ? lineBytes.Length - 1 : lineBytes.Length;
                                    if (length == 0) continue;
                                    Utf8.GetString(lineBytes, 0, length);
                                    _terminalSeen = false; _terminalFailed = false;
                                    await Process.StandardInput.BaseStream.WriteAsync(lineBytes, 0, length, token);
                                    await Process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }, 0, 1, token);
                                    await Process.StandardInput.BaseStream.FlushAsync(token);
                                    _lastSentOffset = chunkStart + index + 1;
                                    lock (_inputGate) _pendingInputOffsets.Enqueue(_lastSentOffset);
                                    WriteInputState();
                                }
                            }
                        }
                    }
                    await Task.Delay(80, token);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { await DelayQuietly(120, token); }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
            }
        }

        private async Task PumpOutputAsync(CancellationToken token)
        {
            var pendingEvents = new List<KeyValuePair<string, string>>(32);
            StreamWriter output = null;
            try
            {
                try
                {
                    output = new StreamWriter(new FileStream(_outputPath, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, 16384, true), new UTF8Encoding(false), 16384) { AutoFlush = true };
                }
                catch (Exception error)
                {
                    CrashLog.Handled("NativeWorkerOutputFile", error);
                    NativeMetrics.RecordWorkerError("output-file-open");
                }
                using (var reader = new StreamReader(Process.StandardOutput.BaseStream, Utf8, false, 16384, true))
                {
                    var readTask = reader.ReadLineAsync();
                    while (!token.IsCancellationRequested)
                    {
                        var completed = await Task.WhenAny(readTask, Task.Delay(55, token));
                        if (completed != readTask)
                        {
                            if (!PersistDurableEvents(pendingEvents)) { FailOutputPersistence(); return; }
                            continue;
                        }
                        var line = await readTask;
                        if (line == null) break;
                        readTask = reader.ReadLineAsync();
                        KeyValuePair<string, string> pendingEvent;
                        var persistedLine = PrepareDurableEvent(line, out pendingEvent);
                        if (_eventStore != null) pendingEvents.Add(pendingEvent);
                        if (output != null)
                        {
                            try { await output.WriteLineAsync(persistedLine); await output.FlushAsync(); }
                            catch (Exception error)
                            {
                                CrashLog.Handled("NativeWorkerOutputFile", error);
                                NativeMetrics.RecordWorkerError("output-file-write");
                                try { output.Dispose(); } catch { }
                                output = null;
                            }
                        }
                        JObject value = null;
                        try { value = JObject.Parse(line); RecordCompactionEvidence(value); }
                        catch (Exception error) { CrashLog.Handled("NativeWorkerOutputParse", error); }
                        var isResult = string.Equals((string)value?["type"], "result", StringComparison.Ordinal);
                        if ((pendingEvents.Count >= 32 || isResult) && !PersistDurableEvents(pendingEvents)) { FailOutputPersistence(); return; }
                        if (!isResult) continue;
                        var isError = (bool?)value["is_error"] ?? false;
                        MarkInputCompleted();
                        bool terminal = false;
                        lock (_inputGate) terminal = _pendingInputOffsets.Count == 0;
                        if (isError)
                        {
                            bool ignored;
                            _streamError = ToolRuntimePolicy.RedactAndLimit((string)value["result"] ?? (string)value["message"] ?? "Claude Code returned an error.", _toolRuntime.MaxErrorBytes, out ignored);
                            if (terminal) WriteStatus("failed", 1, _streamError);
                        }
                        else if (terminal) WriteStatus("completed", 0, "");
                        if (_eventStore != null) _eventStore.TransitionActiveToolCalls(_runId, "failed", "Claude Code ended the turn before the Tool returned a result.");
                        lock (_inputGate) { _terminalSeen = terminal; _terminalFailed = terminal && isError; }
                        if (_eventStore != null) _eventStore.UpdateRunState(_runId, terminal ? (isError ? JobStates.Failed : JobStates.Completed) : JobStates.Running, ProcessId, value.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    if (!PersistDurableEvents(pendingEvents)) FailOutputPersistence();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception error)
            {
                CrashLog.Handled("NativeWorkerOutputPump", error);
                NativeMetrics.RecordWorkerError("output-pump");
                FailOutputPersistence("Worker 输出管道异常：" + error.Message);
            }
            finally { if (output != null) output.Dispose(); }
        }

        private void RecordCompactionEvidence(JObject value)
        {
            if (_eventStore == null || value == null) return;
            var eventType = (string)value["type"] ?? "";
            var subtype = (string)value["subtype"] ?? "";
            var status = (string)value["status"] ?? "";
            var nestedType = (string)value["event"]?["type"] ?? "";
            var signals = new[] { eventType, subtype, status, nestedType };
            if (!signals.Any(signal => signal.IndexOf("compact", StringComparison.OrdinalIgnoreCase) >= 0)) return;

            var details = value["compact_metadata"] as JObject ?? value["compaction"] as JObject ?? new JObject();
            var trigger = FirstText(value, details, "trigger", "reason", "mode", "source");
            var beforeTokens = FirstLong(value, details, "pre_tokens", "preTokens", "before_tokens", "beforeTokens", "original_tokens", "originalTokens");
            var afterTokens = FirstLong(value, details, "post_tokens", "postTokens", "after_tokens", "afterTokens", "compacted_tokens", "compactedTokens");
            var marker = (string)value["uuid"] ?? (string)value["event_id"] ?? "line-" + Interlocked.Read(ref _responseLineOrdinal);
            var manual = trigger.IndexOf("manual", StringComparison.OrdinalIgnoreCase) >= 0 || trigger.IndexOf("user", StringComparison.OrdinalIgnoreCase) >= 0;
            _eventStore.RecordContext(_runId, _taskId, "compaction", "Claude Code 会话压缩", marker, new JObject
            {
                ["mode"] = manual ? "manual" : "auto",
                ["trigger"] = trigger,
                ["eventType"] = eventType,
                ["subtype"] = subtype,
                ["beforeTokens"] = beforeTokens,
                ["afterTokens"] = afterTokens,
                ["nativeAutocompact"] = true,
                ["countedInPrompt"] = false
            });
        }

        private static string FirstText(JObject primary, JObject secondary, params string[] names)
        {
            foreach (var name in names)
            {
                var value = (string)primary?[name] ?? (string)secondary?[name];
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static long FirstLong(JObject primary, JObject secondary, params string[] names)
        {
            foreach (var name in names)
            {
                var token = primary?[name] ?? secondary?[name];
                if (token == null) continue;
                long value;
                if (long.TryParse(token.ToString(), out value) && value > 0) return value;
            }
            return 0L;
        }

        private async Task PumpErrorAsync(CancellationToken token)
        {
            using (var reader = new StreamReader(Process.StandardError.BaseStream, Utf8, false, 4096, true))
            {
                var tail = new StringBuilder();
                var buffer = new char[4096];
                var limit = Math.Max(8192, _toolRuntime.MaxErrorBytes * 2);
                int count;
                while (!token.IsCancellationRequested && (count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    tail.Append(buffer, 0, count);
                    if (tail.Length > limit) tail.Remove(0, tail.Length - limit);
                }
                var text = tail.ToString();
                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(text)) return;
                bool ignored;
                _stderr = ToolRuntimePolicy.RedactAndLimit(text, _toolRuntime.MaxErrorBytes, out ignored);
                File.WriteAllText(_errorPath, _stderr, new UTF8Encoding(false));
            }
        }

        private async Task PumpHeartbeatAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsAlive)
            {
                if (!_paused && _eventStore != null && _runId.Length > 0)
                {
                    var timedOut = _eventStore.MarkTimedOutToolCalls(_runId, _toolRuntime.MaxDurationSeconds);
                    if (timedOut.Count > 0)
                    {
                        var names = string.Join(", ", timedOut.OfType<JObject>().Select(item => (string)item["toolName"] ?? "Tool"));
                        var reason = "Tool runtime timeout: " + names;
                        _eventStore.TransitionActiveToolCalls(_runId, "cancelled", "Run terminated because another Tool exceeded its runtime limit.");
                        _eventStore.RecordContext(_runId, _taskId, "tool-runtime-timeout", "Tool Runtime 超时", "", new JObject
                        {
                            ["tools"] = timedOut.DeepClone(), ["maxDurationSeconds"] = _toolRuntime.MaxDurationSeconds,
                            ["timeoutAction"] = _toolRuntime.TimeoutAction, ["countedInPrompt"] = false
                        });
                        StopInternal(JobStates.Failed, "timed_out", reason, false);
                        break;
                    }
                }
                var heartbeatState = _paused ? JobStates.Paused : _terminalSeen ? (_terminalFailed ? JobStates.Failed : JobStates.Completed) : JobStates.Running;
                WriteHeartbeat(heartbeatState);
                if (_eventStore != null && !_terminalSeen) _eventStore.UpdateRunState(_runId, _paused ? JobStates.Paused : JobStates.Running, ProcessId, "{}");
                await DelayQuietly(2000, token);
            }
        }

        private void OnExited(object sender, EventArgs args)
        {
            try
            {
                WriteHeartbeat("exited");
                if (_terminalSeen || _cancel.IsCancellationRequested) return;
                var code = Process.ExitCode;
                var message = !string.IsNullOrWhiteSpace(_stderr) ? _stderr : !string.IsNullOrWhiteSpace(_streamError) ? _streamError : "Claude Code native worker exited unexpectedly.";
                if (!string.IsNullOrWhiteSpace(message)) File.WriteAllText(_errorPath, message, new UTF8Encoding(false));
                var state = code == 0 ? JobStates.Completed : JobStates.Failed;
                WriteStatus(state, code, code == 0 ? "" : message);
                if (_eventStore != null)
                {
                    _eventStore.TransitionActiveToolCalls(_runId, "failed", code == 0 ? "Worker exited before the Tool returned a result." : message);
                    _eventStore.UpdateRunState(_runId, state, ProcessId, new JObject { ["exitCode"] = code, ["message"] = code == 0 ? "" : message }.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
            catch (Exception error) { CrashLog.Handled("NativeWorkerExit", error); }
        }

        private void WriteStatus(string state, int exitCode, string message)
        {
            lock (_statusGate)
            {
                JsonUtil.WriteAtomic(_statusPath, new JObject
                {
                    ["state"] = state, ["exitCode"] = exitCode, ["message"] = message ?? "",
                    ["worker"] = "native", ["harness"] = _workerHarness, ["pid"] = ProcessId, ["finishedAt"] = ProviderStore.NowIso()
                });
            }
        }

        private void WriteHeartbeat(string state)
        {
            try
            {
                JsonUtil.WriteAtomic(_heartbeatPath, new JObject
                {
                    ["worker"] = "native", ["harness"] = _workerHarness, ["pid"] = ProcessId, ["state"] = state,
                    ["updatedAt"] = ProviderStore.NowIso(), ["leaseUntil"] = DateTimeOffset.Now.AddSeconds(8).ToString("o")
                });
            }
            catch (Exception error) { CrashLog.Handled("NativeWorkerHeartbeat", error); }
        }

        private void WriteInputState()
        {
            try
            {
                lock (_inputGate) JsonUtil.WriteAtomic(_inputStatePath, new JObject
                {
                    ["schemaVersion"] = 1, ["sentOffset"] = _lastSentOffset,
                    ["completedOffset"] = _completedInputOffset, ["updatedAt"] = ProviderStore.NowIso()
                });
            }
            catch (Exception error) { CrashLog.Handled("NativeWorkerInputState", error); }
        }

        private string PrepareDurableEvent(string line, out KeyValuePair<string, string> pendingEvent)
        {
            var persisted = ToolRuntimePolicy.SanitizeWorkerEvent(line, _toolRuntimeManifest);
            pendingEvent = new KeyValuePair<string, string>("", line);
            if (_eventStore == null) return persisted;
            long responseOffset;
            lock (_inputGate)
            {
                responseOffset = _pendingInputOffsets.Count > 0 ? _pendingInputOffsets.Peek() : _lastSentOffset;
                if (_activeResponseOffset != responseOffset) { _activeResponseOffset = responseOffset; Interlocked.Exchange(ref _responseLineOrdinal, 0L); }
            }
            var ordinal = Interlocked.Increment(ref _responseLineOrdinal);
            var eventId = "native:" + _runId + ":input-" + responseOffset + ":line-" + ordinal;
            pendingEvent = new KeyValuePair<string, string>(eventId, line);
            return persisted;
        }

        private bool PersistDurableEvents(List<KeyValuePair<string, string>> events)
        {
            if (events == null || events.Count == 0) return true;
            if (_eventStore == null) { events.Clear(); return true; }
            Exception last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    _eventStore.AppendWorkerEvents(_runId, _taskId, events, _toolRuntimeManifest);
                    events.Clear();
                    return true;
                }
                catch (Exception error) { last = error; Thread.Sleep(30 * (attempt + 1)); }
            }
            CrashLog.Handled("NativeWorkerEventPersistence", new IOException("Failed to append Worker event batch to SQLite after retries.", last));
            NativeMetrics.RecordWorkerError("event-persistence");
            return false;
        }

        private void FailOutputPersistence(string message = "Worker 输出无法可靠写入本地事件存储，任务已停止以避免丢失结果。")
        {
            bool ignored;
            _streamError = ToolRuntimePolicy.RedactAndLimit(message, _toolRuntime.MaxErrorBytes, out ignored);
            try { File.WriteAllText(_errorPath, _streamError, new UTF8Encoding(false)); } catch { }
            StopInternal(JobStates.Failed, "persistence_failed", _streamError, true);
        }

        private void MarkInputCompleted()
        {
            lock (_inputGate)
            {
                var completed = _pendingInputOffsets.Count > 0 ? _pendingInputOffsets.Dequeue() : _lastSentOffset;
                _completedInputOffset = Math.Max(_completedInputOffset, completed);
            }
            WriteInputState();
        }

        private static async Task DelayQuietly(int milliseconds, CancellationToken token)
        {
            try { await Task.Delay(milliseconds, token); } catch (OperationCanceledException) { }
        }

        public void Stop()
        {
            StopInternal(JobStates.Cancelled, "cancelled", "任务已由用户停止", true);
        }

        public void Fail(string reason)
        {
            StopInternal(JobStates.Failed, "failed", string.IsNullOrWhiteSpace(reason) ? "上游模型请求失败" : reason, true);
        }

        // Retire a process that already produced a durable terminal result without
        // rewriting that result as cancelled.
        public void Retire(string terminalState)
        {
            lock (_statusGate)
            {
                if (_stopped || _disposed) return;
                _stopped = true;
            }
            try { Process.StandardInput.Close(); } catch { }
            try { _cancel.Cancel(); } catch { }
            try { if (IsAlive && !Process.WaitForExit(250)) _jobObject.Terminate(0); } catch { }
            try { if (IsAlive) Process.Kill(); } catch { }
            try { if (IsAlive) Process.WaitForExit(1000); } catch { }
            WriteHeartbeat(string.IsNullOrWhiteSpace(terminalState) ? JobStates.Completed : terminalState);
            try
            {
                var tasks = new[] { _outputTask, _errorTask, _inputTask, _heartbeatTask }.Where(task => task != null).ToArray();
                if (tasks.Length > 0) Task.WaitAll(tasks, 1200);
            }
            catch { }
            _disposed = true;
            try { if (_jobObject != null) _jobObject.Dispose(); } catch { }
            try { if (Process != null) Process.Dispose(); } catch { }
            try { _cancel.Dispose(); } catch { }
        }

        private void StopInternal(string runState, string heartbeatState, string reason, bool transitionTools)
        {
            lock (_statusGate)
            {
                if (_stopped) return;
                _stopped = true;
            }
            if (transitionTools && _eventStore != null)
                _eventStore.TransitionActiveToolCalls(_runId, runState == JobStates.Failed ? "failed" : "cancelled", reason);
            try { _cancel.Cancel(); } catch { }
            try { if (_jobObject != null) _jobObject.Terminate(1); } catch { }
            try { if (IsAlive) Process.Kill(); } catch { }
            WriteHeartbeat(heartbeatState);
            WriteStatus(runState, runState == JobStates.Completed ? 0 : 1, reason);
            if (_eventStore != null) _eventStore.UpdateRunState(_runId, runState, ProcessId, new JObject { ["reason"] = reason }.ToString(Newtonsoft.Json.Formatting.None));
        }

        public bool Pause()
        {
            if (_paused) return true;
            if (!IsAlive) return false;
            var status = NtSuspendProcess(Process.Handle);
            if (status != 0) return false;
            _paused = true; WriteHeartbeat(JobStates.Paused);
            if (_eventStore != null) _eventStore.UpdateRunState(_runId, JobStates.Paused, ProcessId, "{}");
            return true;
        }

        public bool Resume()
        {
            if (!_paused) return IsAlive;
            if (!IsAlive) return false;
            var status = NtResumeProcess(Process.Handle);
            if (status != 0) return false;
            _paused = false; WriteHeartbeat(JobStates.Running);
            if (_eventStore != null) _eventStore.UpdateRunState(_runId, JobStates.Running, ProcessId, "{}");
            return true;
        }

        [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            try { if (_jobObject != null) _jobObject.Dispose(); } catch { }
            try { if (Process != null) Process.Dispose(); } catch { }
            _cancel.Dispose();
        }
    }

    internal static class NativeWorkerSelfTest
    {
        public static int Run(string fakeExecutable, string root)
        {
            Directory.CreateDirectory(root);
            try
            {
                JsonUtil.WriteAtomic(AppPaths.SettingsFile, new JObject { ["claudeExecutable"] = fakeExecutable });
                Environment.SetEnvironmentVariable("CLAUDE_GUI_CLAUDE_EXE", "");
                var runtime = NativeWorkerHandle.ClaudeRuntimeDiagnostics(true);
                if (!((bool?)runtime["available"] ?? false) || !((bool?)runtime["probeOk"] ?? false)) return 10;
                if (!string.Equals(Path.GetFullPath(fakeExecutable), (string)runtime["path"], StringComparison.OrdinalIgnoreCase) || (string)runtime["source"] != "settings") return 24;
                var output = Path.Combine(root, "stream.jsonl");
                var error = Path.Combine(root, "error.txt");
                var status = Path.Combine(root, "status.json");
                var pid = Path.Combine(root, "child.pid");
                var input = Path.Combine(root, "input.jsonl");
                File.WriteAllText(output, "", new UTF8Encoding(false));
                File.WriteAllText(error, "", new UTF8Encoding(false));
                File.WriteAllText(input, "", new UTF8Encoding(false));
                var request = new JObject
                {
                    ["workspace"] = root, ["sessionId"] = Guid.NewGuid().ToString(), ["resume"] = false,
                    ["model"] = "offline-model", ["effort"] = "low", ["permissionMode"] = "readonly",
                    ["allowedTools"] = new JArray(), ["disallowedTools"] = new JArray(), ["addDirs"] = new JArray()
                };
                using (var worker = NativeWorkerHandle.Start(request, output, error, status, pid, input))
                {
                    if (!worker.Process.StartInfo.Arguments.Contains("--max-turns 100")) return 25;
                    AppendInput(input, "第一轮中文");
                    if (!WaitForResults(output, 1, 10000)) return 11;
                    AppendInput(input, "第二轮续传");
                    if (!WaitForResults(output, 2, 10000)) return 12;
                    var text = ReadSharedOutput(output);
                    if (!text.Contains("第一轮中文") || !text.Contains("第二轮续传") || text.Contains("�")) return 13;
                    var heartbeat = JsonUtil.Read(Path.Combine(root, "worker-heartbeat.json"), new JObject()) as JObject ?? new JObject();
                    if ((string)heartbeat["worker"] != "native" || (int?)heartbeat["pid"] != worker.ProcessId) return 14;
                    worker.Stop();
                    var expires = DateTime.UtcNow.AddSeconds(5);
                    while (worker.IsAlive && DateTime.UtcNow < expires) Thread.Sleep(50);
                    if (worker.IsAlive) return 15;
                }
                using (var restarted = NativeWorkerHandle.Start(request, output, error, status, pid, input))
                {
                    Thread.Sleep(350);
                    if (CountResults(output) != 2) return 16;
                    AppendInput(input, "第三轮重启后续传");
                    if (!WaitForResults(output, 3, 10000)) return 17;
                    AppendInput(input, "第四轮中断恢复");
                    if (!WaitForUncompletedInput(root, 5000)) return 18;
                    restarted.Stop();
                }
                using (var recovered = NativeWorkerHandle.Start(request, output, error, status, pid, input))
                {
                    if (!WaitForResults(output, 4, 10000)) return 19;
                    var text = ReadSharedOutput(output);
                    if (!text.Contains("第四轮中断恢复") || text.Contains("�")) return 21;
                    AppendInput(input, "第五轮中断恢复暂停");
                    if (!WaitForUncompletedInput(root, 5000) || !recovered.Pause()) return 22;
                    var beforePause = CountResults(output); Thread.Sleep(1500);
                    if (CountResults(output) != beforePause || !recovered.Resume() || !WaitForResults(output, 5, 10000)) return 23;
                    recovered.Stop();
                }
                return 0;
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(root, "selftest-error.txt"), error.ToString(), new UTF8Encoding(false)); } catch { }
                return 20;
            }
            finally { Environment.SetEnvironmentVariable("CLAUDE_GUI_CLAUDE_EXE", null); }
        }

        private static void AppendInput(string path, string text)
        {
            var value = new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user", ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) } };
            File.AppendAllText(path, value.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine, new UTF8Encoding(false));
        }

        private static bool WaitForResults(string path, int expected, int timeoutMs)
        {
            var expires = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < expires)
            {
                try
                {
                    if (CountResults(path) >= expected) return true;
                }
                catch (IOException) { }
                Thread.Sleep(50);
            }
            return false;
        }

        private static int CountResults(string path)
        {
            return File.Exists(path) ? ReadSharedOutput(path).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Contains("\"type\":\"result\"")) : 0;
        }

        private static string ReadSharedOutput(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        private static bool WaitForUncompletedInput(string root, int timeoutMs)
        {
            var path = Path.Combine(root, "worker-input-state.json");
            var expires = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < expires)
            {
                var value = JsonUtil.Read(path, new JObject()) as JObject ?? new JObject();
                if ((long?)value["sentOffset"] > (long?)value["completedOffset"]) return true;
                Thread.Sleep(40);
            }
            return false;
        }
    }

    internal sealed class NativeJobObject : IDisposable
    {
        private IntPtr _handle;
        private NativeJobObject(IntPtr handle) { _handle = handle; }

        public static NativeJobObject Attach(Process process)
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());
            var job = new NativeJobObject(handle);
            try
            {
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x00002000;
                var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                var pointer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, pointer, false);
                    if (!SetInformationJobObject(handle, 9, pointer, (uint)length)) throw new InvalidOperationException("SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
                }
                finally { Marshal.FreeHGlobal(pointer); }
                if (!AssignProcessToJobObject(handle, process.Handle)) throw new InvalidOperationException("AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
                return job;
            }
            catch { job.Dispose(); throw; }
        }

        public void Terminate(uint code) { if (_handle != IntPtr.Zero) TerminateJobObject(_handle, code); }
        public void Dispose() { if (_handle == IntPtr.Zero) return; CloseHandle(_handle); _handle = IntPtr.Zero; }

        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}

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
        private bool _terminalSeen;
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
            var executable = Environment.GetEnvironmentVariable("CLAUDE_GUI_CLAUDE_EXE");
            if (string.IsNullOrWhiteSpace(executable)) executable = Path.Combine(AppPaths.ClaudeRoot, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Claude Code executable not found", executable);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", BuildArguments(_request).Select(QuoteArgument)),
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
            ApplyEnvironment(startInfo, _request);
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

        private static List<string> BuildArguments(JObject request)
        {
            var args = new List<string>
            {
                "-p", "--input-format", "stream-json", "--output-format", "stream-json",
                "--include-partial-messages", "--include-hook-events", "--forward-subagent-text", "--replay-user-messages",
                "--autocompact", "auto", "--verbose", "--max-turns", "30",
                "--model", (string)request["model"] ?? "", "--effort", (string)request["effort"] ?? "high"
            };
            if ((bool?)request["resume"] ?? false) { args.Add("--resume"); args.Add((string)request["sessionId"] ?? ""); }
            else { args.Add("--session-id"); args.Add((string)request["sessionId"] ?? ""); }
            var permissionMode = ((string)request["permissionMode"] ?? "readonly").ToLowerInvariant();
            var permissionConfig = (string)request["permissionMcpConfig"] ?? "";
            if (permissionConfig.Length > 0)
            {
                args.Add("--permission-mode"); args.Add("manual"); args.Add("--mcp-config"); args.Add(permissionConfig);
                args.Add("--allowedTools"); args.Add("mcp__gui_permissions__approval_prompt");
                args.Add("--permission-prompt-tool"); args.Add("mcp__gui_permissions__approval_prompt");
            }
            else if (permissionMode == "plan") { args.Add("--permission-mode"); args.Add("plan"); }
            else if (permissionMode == "full") args.Add("--dangerously-skip-permissions");
            else
            {
                args.Add("--permission-mode"); args.Add("dontAsk");
                if (permissionMode != "scoped") { args.Add("--tools"); args.Add("Read,Glob,Grep,WebSearch,WebFetch,Skill"); }
            }
            AddRepeated(args, "--allowed-tools", request["allowedTools"] as JArray);
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

        private static string QuoteArgument(string value)
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
                startInfo.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"] = SecretStore.Unprotect((string)provider["tokenEncrypted"]);
                startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = "";
                if (!string.IsNullOrWhiteSpace((string)provider["baseUrl"])) startInfo.EnvironmentVariables["ANTHROPIC_BASE_URL"] = (string)provider["baseUrl"];
            }
            startInfo.EnvironmentVariables["PYTHONUTF8"] = "1";
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
                                    _terminalSeen = false;
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
            using (var reader = new StreamReader(Process.StandardOutput.BaseStream, Utf8, false, 8192, true))
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    AppendDurableEvent(line);
                    File.AppendAllText(_outputPath, line + Environment.NewLine, new UTF8Encoding(false));
                    try
                    {
                        var value = JObject.Parse(line);
                        if ((string)value["type"] != "result") continue;
                        var isError = (bool?)value["is_error"] ?? false;
                        MarkInputCompleted();
                        if (isError)
                        {
                            _streamError = (string)value["result"] ?? (string)value["message"] ?? "Claude Code returned an error.";
                            WriteStatus("failed", 1, _streamError);
                        }
                        else WriteStatus("completed", 0, "");
                        lock (_inputGate) _terminalSeen = _pendingInputOffsets.Count == 0;
                        if (_eventStore != null) _eventStore.UpdateRunState(_runId, isError ? JobStates.Failed : JobStates.Completed, ProcessId, value.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    catch (Exception error) { CrashLog.Write("NativeWorkerOutput", error); }
                }
            }
        }

        private async Task PumpErrorAsync(CancellationToken token)
        {
            using (var reader = new StreamReader(Process.StandardError.BaseStream, Utf8, false, 4096, true))
            {
                var text = await reader.ReadToEndAsync();
                if (token.IsCancellationRequested || string.IsNullOrWhiteSpace(text)) return;
                _stderr = text;
                File.WriteAllText(_errorPath, text, new UTF8Encoding(false));
            }
        }

        private async Task PumpHeartbeatAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsAlive)
            {
                WriteHeartbeat(_paused ? JobStates.Paused : _terminalSeen ? "idle" : "running");
                if (_eventStore != null) _eventStore.UpdateRunState(_runId, _paused ? JobStates.Paused : _terminalSeen ? "idle" : JobStates.Running, ProcessId, "{}");
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
                WriteStatus(code == 0 ? "completed" : "failed", code, code == 0 ? "" : message);
            }
            catch (Exception error) { CrashLog.Write("NativeWorkerExit", error); }
        }

        private void WriteStatus(string state, int exitCode, string message)
        {
            lock (_statusGate)
            {
                JsonUtil.WriteAtomic(_statusPath, new JObject
                {
                    ["state"] = state, ["exitCode"] = exitCode, ["message"] = message ?? "",
                    ["worker"] = "native", ["pid"] = ProcessId, ["finishedAt"] = ProviderStore.NowIso()
                });
            }
        }

        private void WriteHeartbeat(string state)
        {
            try
            {
                JsonUtil.WriteAtomic(_heartbeatPath, new JObject
                {
                    ["worker"] = "native", ["pid"] = ProcessId, ["state"] = state,
                    ["updatedAt"] = ProviderStore.NowIso(), ["leaseUntil"] = DateTimeOffset.Now.AddSeconds(8).ToString("o")
                });
            }
            catch (Exception error) { CrashLog.Write("NativeWorkerHeartbeat", error); }
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
            catch (Exception error) { CrashLog.Write("NativeWorkerInputState", error); }
        }

        private void AppendDurableEvent(string line)
        {
            if (_eventStore == null) return;
            long responseOffset;
            lock (_inputGate)
            {
                responseOffset = _pendingInputOffsets.Count > 0 ? _pendingInputOffsets.Peek() : _lastSentOffset;
                if (_activeResponseOffset != responseOffset) { _activeResponseOffset = responseOffset; Interlocked.Exchange(ref _responseLineOrdinal, 0L); }
            }
            var ordinal = Interlocked.Increment(ref _responseLineOrdinal);
            var eventId = "native:" + _runId + ":input-" + responseOffset + ":line-" + ordinal;
            Exception last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try { _eventStore.AppendWorkerEvent(_runId, _taskId, line, eventId); return; }
                catch (Exception error) { last = error; Thread.Sleep(30 * (attempt + 1)); }
            }
            throw new IOException("Failed to append Worker event to SQLite after retries.", last);
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
            if (_stopped) return;
            _stopped = true;
            try { _cancel.Cancel(); } catch { }
            try { if (_jobObject != null) _jobObject.Terminate(1); } catch { }
            try { if (IsAlive) Process.Kill(); } catch { }
            WriteHeartbeat("cancelled");
            if (_eventStore != null) _eventStore.UpdateRunState(_runId, JobStates.Cancelled, ProcessId, "{}");
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
            Environment.SetEnvironmentVariable("CLAUDE_GUI_CLAUDE_EXE", fakeExecutable);
            try
            {
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
                    AppendInput(input, "第一轮中文");
                    if (!WaitForResults(output, 1, 10000)) return 11;
                    AppendInput(input, "第二轮续传");
                    if (!WaitForResults(output, 2, 10000)) return 12;
                    var text = File.ReadAllText(output, Encoding.UTF8);
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
                    var text = File.ReadAllText(output, Encoding.UTF8);
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
            return File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).Count(line => line.Contains("\"type\":\"result\"")) : 0;
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

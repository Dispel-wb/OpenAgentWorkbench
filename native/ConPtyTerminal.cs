using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCodeWorkbench
{
    internal sealed class TerminalShellProfile
    {
        public string Id;
        public string Name;
        public string Executable;
        public string Arguments;
    }

    internal static class ConPtyTerminalSelfTest
    {
        public static bool Run(string resultPath, string workspace)
        {
            try
            {
                var suffix = Guid.NewGuid().ToString("N");
                var marker = "CLAUDE_CONPTY_SELFTEST_" + suffix;
                var output = new StringBuilder();
                using (var session = new ConPtyTerminalSession(Path.GetFullPath(workspace), 96, 28))
                {
                    session.Resize(101, 31);
                    if (session.ShellId == "cmd") session.Write("set WORKBENCH_TEST_SUFFIX=" + suffix + "&echo CLAUDE_CONPTY_SELFTEST_%WORKBENCH_TEST_SUFFIX%");
                    else session.Write("Write-Output ('CLAUDE_CONPTY_SELFTEST_' + '" + suffix + "')");
                    var expires = DateTime.UtcNow.AddSeconds(6);
                    while (DateTime.UtcNow < expires && output.ToString().IndexOf(marker, StringComparison.Ordinal) < 0)
                    {
                        Thread.Sleep(80);
                        output.Append(session.Read());
                    }
                }
                var ok = output.ToString().IndexOf(marker, StringComparison.Ordinal) >= 0;
                File.WriteAllText(resultPath, (ok ? "OK\n" : "FAILED\n") + output, new UTF8Encoding(false));
                return ok;
            }
            catch (Exception error)
            {
                try { File.WriteAllText(resultPath, "FAILED\n" + error, new UTF8Encoding(false)); } catch { }
                return false;
            }
        }
    }

    internal sealed class ConPtyTerminalSession : IDisposable
    {
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private readonly object _gate = new object();
        private readonly Queue<string> _output = new Queue<string>();
        private readonly FileStream _input;
        private readonly FileStream _outputStream;
        private readonly Process _process;
        private readonly NativeJobObject _jobObject;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private IntPtr _pseudoConsole;
        private bool _disposed;

        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly string Workspace;
        public readonly string ShellId;
        public readonly string ShellName;
        public bool Alive { get { try { return !_disposed && _process != null && !_process.HasExited; } catch { return false; } } }

        public ConPtyTerminalSession(string workspace, short columns = 120, short rows = 36, string shell = "powershell")
        {
            Workspace = workspace;
            var profile = ResolveShell(shell);
            ShellId = profile.Id;
            ShellName = profile.Name;
            IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            var processInfo = new ProcessInformation();
            try
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
                Check(CreatePipe(out inputRead, out inputWrite, ref security, 0), "CreatePipe(input)");
                Check(CreatePipe(out outputRead, out outputWrite, ref security, 0), "CreatePipe(output)");
                Check(CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out _pseudoConsole) == 0, "CreatePseudoConsole");

                IntPtr bytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributeList = Marshal.AllocHGlobal(bytes);
                Check(InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes), "InitializeProcThreadAttributeList");
                // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE expects HPCON itself as lpValue.
                // Passing a pointer to a second copy produces cmd.exe startup error 0xc0000142.
                Check(UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");

                var startup = new StartupInfoEx(); startup.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx)); startup.AttributeList = attributeList;
                // /D disables per-user cmd AutoRun hooks. Those hooks are unnecessary in the
                // embedded terminal and some shell injectors fail to initialise under ConPTY.
                var command = new StringBuilder("\"" + profile.Executable + "\"" + (profile.Arguments.Length > 0 ? " " + profile.Arguments : ""));
                Check(CreateProcess(profile.Executable, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workspace, ref startup, out processInfo), "CreateProcess(" + profile.Id + ")");
                // Microsoft requires the ConPTY-facing pipe ends to remain open until
                // the hosted process has connected, then be released by this host.
                CloseHandle(inputRead); inputRead = IntPtr.Zero; CloseHandle(outputWrite); outputWrite = IntPtr.Zero;
                _process = Process.GetProcessById((int)processInfo.ProcessId);
                _process.EnableRaisingEvents = true; _process.Exited += (sender, args) => Enqueue("\r\n[ConPTY 已退出]\r\n");
                _jobObject = NativeJobObject.Attach(_process);
                CloseHandle(processInfo.Thread); processInfo.Thread = IntPtr.Zero; CloseHandle(processInfo.Process); processInfo.Process = IntPtr.Zero;
                // CreatePipe returns synchronous handles. FileStream may still expose ReadAsync,
                // but it must not be told that the underlying handle was opened for OVERLAPPED I/O.
                _input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 4096, false); inputWrite = IntPtr.Zero;
                _outputStream = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 4096, false); outputRead = IntPtr.Zero;
                Task.Run((Func<Task>)PumpOutput);
                if (ShellId == "cmd") Write("chcp 65001>nul");
                else if (ShellId == "powershell" || ShellId == "pwsh")
                    Write("[Console]::InputEncoding=New-Object Text.UTF8Encoding $false;[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false;$OutputEncoding=[Console]::OutputEncoding");
            }
            catch
            {
                if (_pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
                throw;
            }
            finally
            {
                if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
                foreach (var handle in new[] { inputRead, inputWrite, outputRead, outputWrite, processInfo.Process, processInfo.Thread }) if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        private async Task PumpOutput()
        {
            var buffer = new byte[8192];
            try
            {
                while (!_disposed)
                {
                    var count = await _outputStream.ReadAsync(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
                    var charCount = _decoder.GetChars(buffer, 0, count, chars, 0, false);
                    Enqueue(new string(chars, 0, charCount));
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException error) { if (!_disposed) Enqueue("\r\n[ConPTY 读取错误：" + error.Message + "]\r\n"); }
        }

        private void Enqueue(string value) { lock (_gate) { _output.Enqueue(value); while (_output.Count > 5000) _output.Dequeue(); } }
        public string Read() { lock (_gate) { var result = new StringBuilder(); while (_output.Count > 0) result.Append(_output.Dequeue()); return result.ToString(); } }
        public void Write(string command)
        {
            WriteRaw((command ?? "") + "\r");
        }
        public void WriteRaw(string value)
        {
            if (!Alive) throw new InvalidOperationException("ConPTY 终端已经退出");
            var bytes = Encoding.UTF8.GetBytes(value ?? ""); _input.Write(bytes, 0, bytes.Length); _input.Flush();
        }
        public void Resize(short columns, short rows) { if (_pseudoConsole != IntPtr.Zero) Check(ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows)) == 0, "ResizePseudoConsole"); }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            try { if (_jobObject != null) _jobObject.Terminate(0); } catch { }
            try { _input.Dispose(); } catch { } try { _outputStream.Dispose(); } catch { }
            try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
            try { if (_process != null) _process.Dispose(); } catch { }
            try { if (_jobObject != null) _jobObject.Dispose(); } catch { }
            if (_pseudoConsole != IntPtr.Zero) { ClosePseudoConsole(_pseudoConsole); _pseudoConsole = IntPtr.Zero; }
        }

        public static TerminalShellProfile[] AvailableShells()
        {
            var profiles = new List<TerminalShellProfile>();
            var system = Environment.SystemDirectory;
            // The desktop process can inherit a third-party PSModulePath. On machines
            // using AllSigned this may block the embedded shell behind a publisher
            // trust prompt before the first command is accepted. The override is
            // process-scoped to this child shell and never changes the user's policy.
            AddProfile(profiles, "powershell", "Windows PowerShell", Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass");
            var pwsh = FindPwsh();
            AddProfile(profiles, "pwsh", "PowerShell 7", pwsh, "-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass");
            AddProfile(profiles, "cmd", "Command Prompt", Path.Combine(system, "cmd.exe"), "/D /Q /K");
            AddProfile(profiles, "wsl", "WSL", Path.Combine(system, "wsl.exe"), "");
            return profiles.ToArray();
        }

        private static TerminalShellProfile ResolveShell(string id)
        {
            var profiles = AvailableShells();
            var selected = profiles.FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.OrdinalIgnoreCase));
            if (selected != null) return selected;
            selected = profiles.FirstOrDefault(value => value.Id == "powershell") ?? profiles.FirstOrDefault(value => value.Id == "cmd");
            if (selected == null) throw new InvalidOperationException("没有可用的终端 Shell");
            return selected;
        }

        private static void AddProfile(List<TerminalShellProfile> profiles, string id, string name, string executable, string arguments)
        {
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                profiles.Add(new TerminalShellProfile { Id = id, Name = name, Executable = executable, Arguments = arguments ?? "" });
        }

        private static string FindPwsh()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell");
            if (!Directory.Exists(root)) return "";
            try
            {
                return Directory.EnumerateFiles(root, "pwsh.exe", SearchOption.AllDirectories)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
            }
            catch { return ""; }
        }
        private static void Check(bool success, string operation) { if (!success) throw new InvalidOperationException(operation + " 失败，Win32=" + Marshal.GetLastWin32Error()); }

        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X; public short Y; public Coord(short x, short y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int cb; public string reserved; public string desktop; public string title; public int x, y, xSize, ySize, xChars, yChars, fillAttribute, flags; public short showWindow, reserved2; public IntPtr reservedPointer, stdInput, stdOutput, stdError; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    }
}

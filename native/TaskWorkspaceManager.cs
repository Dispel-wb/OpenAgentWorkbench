using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class TaskWorkspace
    {
        public string Kind;
        public string SourceWorkspace;
        public string WorkerWorkspace;
        public string DescriptorPath;
        public JObject Descriptor;
    }

    internal static class TaskWorkspaceManager
    {
        public static TaskWorkspace Prepare(string sourceWorkspace, string jobId, string runDir, bool mutable, string taskId = null)
        {
            sourceWorkspace = Path.GetFullPath(sourceWorkspace);
            var descriptorPath = Path.Combine(runDir, "isolation.json");
            var existing = JsonUtil.Read(descriptorPath, new JObject()) as JObject ?? new JObject();
            var existingWorker = (string)existing["workerWorkspace"] ?? "";
            if (existingWorker.Length > 0 && Directory.Exists(existingWorker)) return From(existing, descriptorPath);
            if (!mutable)
            {
                var direct = NewDescriptor("direct-readonly", sourceWorkspace, sourceWorkspace, jobId);
                JsonUtil.WriteAtomic(descriptorPath, direct); return From(direct, descriptorPath);
            }

            var repoRoot = GitOutput(sourceWorkspace, "rev-parse --show-toplevel", 12000).Trim();
            if (repoRoot.Length > 0 && Directory.Exists(repoRoot))
            {
                repoRoot = Path.GetFullPath(repoRoot);
                var relative = Relative(repoRoot, sourceWorkspace);
                var hash = ShortHash(repoRoot);
                var worktreeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EditionInfo.StorageId, "worktrees", hash, Safe(string.IsNullOrWhiteSpace(taskId) ? jobId : taskId));
                Directory.CreateDirectory(Path.GetDirectoryName(worktreeRoot));
                if (!Directory.Exists(worktreeRoot) || !Directory.EnumerateFileSystemEntries(worktreeRoot).Any())
                {
                    if (Directory.Exists(worktreeRoot)) Directory.Delete(worktreeRoot, true);
                    var result = Git(repoRoot, "worktree add --detach " + Quote(worktreeRoot) + " HEAD", 60000);
                    if (result.ExitCode != 0) throw new InvalidOperationException("无法创建任务级 Git worktree：" + NonEmpty(result.Error, result.Output));
                }
                var worker = relative.Length == 0 ? worktreeRoot : Path.Combine(worktreeRoot, relative);
                if (!Directory.Exists(worker)) throw new DirectoryNotFoundException("任务 worktree 中缺少原工作目录：" + worker);
                var descriptor = NewDescriptor("git-worktree", sourceWorkspace, worker, jobId);
                descriptor["taskId"] = taskId ?? jobId;
                descriptor["repoRoot"] = repoRoot; descriptor["worktreeRoot"] = worktreeRoot;
                descriptor["baseCommit"] = GitOutput(repoRoot, "rev-parse HEAD", 12000).Trim();
                descriptor["state"] = "isolated";
                JsonUtil.WriteAtomic(descriptorPath, descriptor);
                return From(descriptor, descriptorPath);
            }

            var snapshotRoot = Path.Combine(runDir, "workspace-snapshot");
            var stats = Snapshot(sourceWorkspace, snapshotRoot);
            var plain = NewDescriptor("filesystem-snapshot", sourceWorkspace, sourceWorkspace, jobId);
            plain["snapshotRoot"] = snapshotRoot; plain["snapshot"] = stats; plain["state"] = "isolated";
            JsonUtil.WriteAtomic(descriptorPath, plain);
            return From(plain, descriptorPath);
        }

        public static JObject Describe(string jobId)
        {
            var path = Path.Combine(AppPaths.Runs, Safe(jobId), "isolation.json");
            var descriptor = JsonUtil.Read(path, new JObject()) as JObject ?? new JObject();
            if (descriptor.Count == 0) throw new FileNotFoundException("任务隔离信息不存在");
            if ((string)descriptor["kind"] == "git-worktree") descriptor["changes"] = GitChanges((string)descriptor["worktreeRoot"]);
            else if ((string)descriptor["kind"] == "filesystem-snapshot") descriptor["changes"] = SnapshotChanges(descriptor);
            return descriptor;
        }

        public static JObject Apply(string jobId, JArray paths)
        {
            var descriptor = Describe(jobId);
            if ((string)descriptor["kind"] != "git-worktree") throw new InvalidOperationException("只有 Git worktree 支持应用变更；非 Git 项目请使用恢复快照。");
            var source = (string)descriptor["repoRoot"];
            var worktree = (string)descriptor["worktreeRoot"];
            var selected = (paths ?? new JArray()).Select(value => ((string)value ?? "").Trim()).Where(value => value.Length > 0).ToArray();
            var selector = selected.Length == 0 ? "" : " -- " + string.Join(" ", selected.Select(Quote));
            var patch = GitOutput(worktree, "diff --binary HEAD" + selector, 30000);
            var untracked = GitOutput(worktree, "ls-files --others --exclude-standard" + selector, 12000)
                .Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (patch.Length > 0)
            {
                var patchPath = Path.Combine(AppPaths.Runs, Safe(jobId), "apply.patch");
                File.WriteAllText(patchPath, patch, new UTF8Encoding(false));
                var applied = Git(source, "apply --3way --whitespace=nowarn " + Quote(patchPath), 60000);
                if (applied.ExitCode != 0) throw new InvalidOperationException("源工作区已有冲突，未覆盖任何文件：" + NonEmpty(applied.Error, applied.Output));
            }
            var copied = new JArray();
            foreach (var relative in untracked)
            {
                var from = Inside(worktree, relative); var to = Inside(source, relative);
                if (File.Exists(to)) throw new IOException("源工作区已存在同名文件，未覆盖：" + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(from, to, false); copied.Add(relative);
            }
            descriptor["state"] = "applied"; descriptor["appliedAt"] = ProviderStore.NowIso();
            JsonUtil.WriteAtomic(Path.Combine(AppPaths.Runs, Safe(jobId), "isolation.json"), descriptor);
            return new JObject { ["ok"] = true, ["patchBytes"] = Encoding.UTF8.GetByteCount(patch), ["untrackedCopied"] = copied, ["status"] = GitStatus(source) };
        }

        public static JObject Revert(string jobId)
        {
            var descriptor = Describe(jobId);
            var kind = (string)descriptor["kind"];
            if (kind == "git-worktree")
            {
                var root = (string)descriptor["worktreeRoot"];
                var restore = Git(root, "restore --staged --worktree .", 30000);
                if (restore.ExitCode != 0) throw new InvalidOperationException(NonEmpty(restore.Error, restore.Output));
                foreach (var file in GitOutput(root, "ls-files --others --exclude-standard", 12000).Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var target = Inside(root, file); if (File.Exists(target)) File.Delete(target);
                }
            }
            else if (kind == "filesystem-snapshot") RestoreSnapshot(descriptor);
            else throw new InvalidOperationException("该任务没有可恢复的写入隔离层");
            descriptor["state"] = "reverted"; descriptor["revertedAt"] = ProviderStore.NowIso();
            JsonUtil.WriteAtomic(Path.Combine(AppPaths.Runs, Safe(jobId), "isolation.json"), descriptor);
            return new JObject { ["ok"] = true, ["state"] = "reverted" };
        }

        private static JObject Snapshot(string source, string target)
        {
            Directory.CreateDirectory(target);
            long bytes = 0; var files = 0;
            var sourcePrefix = source.TrimEnd('\\') + "\\";
            var dataPrefix = Path.GetFullPath(AppPaths.Data).TrimEnd('\\') + "\\";
            var pending = new Stack<string>(); pending.Push(source);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var full = Path.GetFullPath(entry);
                    if (full.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(full), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("非 Git 工作区包含链接/重解析点，无法保证完整回滚：" + full);
                    var relative = full.Substring(sourcePrefix.Length); var destination = Path.Combine(target, relative);
                    if (Directory.Exists(full)) { Directory.CreateDirectory(destination); pending.Push(full); continue; }
                    var length = new FileInfo(full).Length; bytes += length; files++;
                    if (files > 100000 || bytes > 2L * 1024 * 1024 * 1024) throw new InvalidOperationException("非 Git 工作区超过安全快照上限（100000 文件或 2 GB），请先初始化 Git。 ");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(full, destination, true);
                }
            }
            return new JObject { ["complete"] = true, ["files"] = files, ["bytes"] = bytes, ["createdAt"] = ProviderStore.NowIso() };
        }

        private static void RestoreSnapshot(JObject descriptor)
        {
            var source = Path.GetFullPath((string)descriptor["sourceWorkspace"]); var snapshot = Path.GetFullPath((string)descriptor["snapshotRoot"]);
            if (!Directory.Exists(snapshot)) throw new DirectoryNotFoundException("任务快照不存在");
            var dataPrefix = Path.GetFullPath(AppPaths.Data).TrimEnd('\\') + "\\";
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray())
            {
                var full = Path.GetFullPath(file); if (full.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase) || full.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var relative = Relative(source, full); if (!File.Exists(Path.Combine(snapshot, relative))) File.Delete(full);
            }
            foreach (var file in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories))
            {
                var relative = Relative(snapshot, file); var target = Inside(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file, target, true);
            }
        }

        private static JArray SnapshotChanges(JObject descriptor)
        {
            var source = (string)descriptor["sourceWorkspace"]; var snapshot = (string)descriptor["snapshotRoot"]; var values = new JArray();
            var paths = new HashSet<string>(Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(path => Relative(source, path)) : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(snapshot)) foreach (var path in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)) paths.Add(Relative(snapshot, path));
            foreach (var relative in paths.Take(5000))
            {
                var current = Path.Combine(source, relative); var before = Path.Combine(snapshot, relative);
                var state = !File.Exists(before) ? "added" : !File.Exists(current) ? "deleted" : SameFile(before, current) ? "unchanged" : "modified";
                if (state != "unchanged") values.Add(new JObject { ["path"] = relative, ["state"] = state });
            }
            return values;
        }

        private static JArray GitChanges(string root)
        {
            var lines = GitOutput(root, "status --porcelain=v1", 12000).Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return new JArray(lines.Select(line => new JObject { ["state"] = line.Length >= 2 ? line.Substring(0, 2) : "??", ["path"] = line.Length > 3 ? line.Substring(3) : line }));
        }
        private static JObject GitStatus(string root) { return new JObject { ["porcelain"] = GitOutput(root, "status --porcelain=v1 -b", 12000) }; }
        private static bool SameFile(string left, string right) { var a = new FileInfo(left); var b = new FileInfo(right); if (a.Length != b.Length) return false; using (var sha = SHA256.Create()) using (var x = File.OpenRead(left)) using (var y = File.OpenRead(right)) return sha.ComputeHash(x).SequenceEqual(sha.ComputeHash(y)); }
        private static string ShortHash(string value) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant())).Take(8).Select(item => item.ToString("x2"))); }
        private static string Safe(string value) { var result = new string((value ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray()); if (result.Length == 0) throw new InvalidOperationException("任务 ID 无效"); return result; }
        private static string Inside(string root, string relative) { var full = Path.GetFullPath(Path.Combine(root, relative)); var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\"; if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("路径越过任务隔离根"); return full; }
        private static string Relative(string root, string path) { var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\"; var full = Path.GetFullPath(path); return string.Equals(full.TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "" : full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full.Substring(prefix.Length) : throw new UnauthorizedAccessException("路径不属于根目录"); }
        private static JObject NewDescriptor(string kind, string source, string worker, string jobId) { return new JObject { ["schemaVersion"] = 1, ["jobId"] = jobId, ["kind"] = kind, ["sourceWorkspace"] = source, ["workerWorkspace"] = worker, ["createdAt"] = ProviderStore.NowIso() }; }
        private static TaskWorkspace From(JObject value, string path) { return new TaskWorkspace { Kind = (string)value["kind"], SourceWorkspace = (string)value["sourceWorkspace"], WorkerWorkspace = (string)value["workerWorkspace"], DescriptorPath = path, Descriptor = value }; }
        private static string Quote(string value) { return "\"" + (value ?? "").Replace("\"", "\\\"") + "\""; }
        private static string NonEmpty(string first, string second) { return !string.IsNullOrWhiteSpace(first) ? first.Trim() : (second ?? "").Trim(); }
        private static string GitOutput(string root, string args, int timeout) { var result = Git(root, args, timeout); return result.ExitCode == 0 ? result.Output : ""; }
        private static WorkspaceCommand Git(string root, string args, int timeout)
        {
            try
            {
                var info = new ProcessStartInfo("git.exe", args) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                using (var process = Process.Start(info))
                {
                    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeout)) { try { process.Kill(); } catch { } return new WorkspaceCommand { ExitCode = -1, Error = "Git operation timed out" }; }
                    return new WorkspaceCommand { ExitCode = process.ExitCode, Output = output.Result ?? "", Error = error.Result ?? "" };
                }
            }
            catch (Exception error) { return new WorkspaceCommand { ExitCode = -1, Error = error.Message }; }
        }
        private sealed class WorkspaceCommand { public int ExitCode; public string Output = ""; public string Error = ""; }
    }

    internal static class TaskWorkspaceSelfTest
    {
        public static int Run(string root)
        {
            try
            {
                var gitRoot = Path.Combine(root, "git-source"); Directory.CreateDirectory(gitRoot);
                if (Exec(gitRoot, "init").ExitCode != 0) return 51;
                Exec(gitRoot, "config user.email workbench@example.invalid"); Exec(gitRoot, "config user.name Workbench-Test");
                File.WriteAllText(Path.Combine(gitRoot, "tracked.txt"), "before", new UTF8Encoding(false));
                Exec(gitRoot, "add tracked.txt"); if (Exec(gitRoot, "commit -m initial").ExitCode != 0) return 52;
                var runId = "git-run"; var runDir = Path.Combine(AppPaths.Runs, runId); Directory.CreateDirectory(runDir);
                var isolated = TaskWorkspaceManager.Prepare(gitRoot, runId, runDir, true, "workspace-selftest-task");
                if (isolated.Kind != "git-worktree" || string.Equals(isolated.WorkerWorkspace, gitRoot, StringComparison.OrdinalIgnoreCase)) return 53;
                File.WriteAllText(Path.Combine(isolated.WorkerWorkspace, "tracked.txt"), "after", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(isolated.WorkerWorkspace, "new.txt"), "new", new UTF8Encoding(false));
                var described = TaskWorkspaceManager.Describe(runId);
                if ((described["changes"] as JArray ?? new JArray()).Count < 2) return 54;
                TaskWorkspaceManager.Apply(runId, new JArray());
                if (File.ReadAllText(Path.Combine(gitRoot, "tracked.txt"), Encoding.UTF8) != "after" || !File.Exists(Path.Combine(gitRoot, "new.txt"))) return 55;
                TaskWorkspaceManager.Revert(runId);
                if (File.ReadAllText(Path.Combine(isolated.WorkerWorkspace, "tracked.txt"), Encoding.UTF8) != "before") return 56;

                var plainRoot = Path.Combine(root, "plain-source"); Directory.CreateDirectory(plainRoot);
                File.WriteAllText(Path.Combine(plainRoot, "plain.txt"), "plain-before", new UTF8Encoding(false));
                var plainRun = "plain-run"; var plainRunDir = Path.Combine(AppPaths.Runs, plainRun); Directory.CreateDirectory(plainRunDir);
                TaskWorkspaceManager.Prepare(plainRoot, plainRun, plainRunDir, true, "plain-task");
                File.WriteAllText(Path.Combine(plainRoot, "plain.txt"), "plain-after", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(plainRoot, "added.txt"), "added", new UTF8Encoding(false));
                if ((TaskWorkspaceManager.Describe(plainRun)["changes"] as JArray ?? new JArray()).Count != 2) return 57;
                TaskWorkspaceManager.Revert(plainRun);
                if (File.ReadAllText(Path.Combine(plainRoot, "plain.txt"), Encoding.UTF8) != "plain-before" || File.Exists(Path.Combine(plainRoot, "added.txt"))) return 58;
                Exec(gitRoot, "worktree remove --force " + Q((string)isolated.Descriptor["worktreeRoot"]));
                return 0;
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(root, "workspace-selftest-error.txt"), error.ToString(), new UTF8Encoding(false)); } catch { }
                return 59;
            }
        }
        private static WorkspaceResult Exec(string root, string arguments)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo("git.exe", arguments) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                { var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit(30000); return new WorkspaceResult { ExitCode = process.ExitCode, Output = output, Error = error }; }
            }
            catch (Exception error) { return new WorkspaceResult { ExitCode = -1, Error = error.Message }; }
        }
        private static string Q(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        private sealed class WorkspaceResult { public int ExitCode; public string Output; public string Error; }
    }
}

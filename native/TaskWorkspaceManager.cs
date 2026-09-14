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
        private static readonly object Gate = new object();

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
                descriptor["baseCommit"] = GitOutput(worktreeRoot, "rev-parse HEAD", 12000).Trim();
                descriptor["state"] = "isolated";
                descriptor["applyJournal"] = new JArray();
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

        public static JObject Diff(string jobId, string requestedPath)
        {
            lock (Gate)
            {
                var descriptor = Describe(jobId);
                if ((string)descriptor["kind"] != "git-worktree") throw new InvalidOperationException("只有 Git worktree 支持 Diff Review");
                var changes = descriptor["changes"] as JArray ?? new JArray();
                var path = (requestedPath ?? "").Trim();
                var change = changes.OfType<JObject>().FirstOrDefault(item => string.Equals((string)item["path"], path, StringComparison.Ordinal));
                if (change == null) throw new FileNotFoundException("所选文件不在当前任务变更中");
                var worktree = (string)descriptor["worktreeRoot"];
                string diff;
                if (string.Equals((string)change["state"], "??", StringComparison.Ordinal))
                {
                    var file = Inside(worktree, path);
                    var info = new FileInfo(file);
                    if (!info.Exists) throw new FileNotFoundException("任务隔离区中的文件不存在", file);
                    if (info.Length > 1024 * 1024) diff = "新增二进制或大型文件 · " + info.Length + " B\n";
                    else
                    {
                        var bytes = File.ReadAllBytes(file);
                        diff = bytes.Any(value => value == 0) ? "新增二进制文件 · " + bytes.Length + " B\n" : "+++ " + path + "\n" + Encoding.UTF8.GetString(bytes);
                    }
                }
                else
                {
                    diff = GitOutput(worktree, "diff --binary --full-index HEAD -- " + ChangeSelector(change), 30000);
                    if (diff.Length == 0) diff = "该文件没有可显示的未提交 Diff。";
                }
                var truncated = diff.Length > 500000;
                if (truncated) diff = diff.Substring(0, 500000) + "\n…Diff 已在 500000 字符处截断…";
                return new JObject { ["jobId"] = jobId, ["path"] = path, ["state"] = change["state"], ["diff"] = diff, ["truncated"] = truncated };
            }
        }

        public static JObject Apply(string jobId, JArray paths)
        {
            lock (Gate)
            {
                var descriptor = Describe(jobId);
                if ((string)descriptor["kind"] != "git-worktree") throw new InvalidOperationException("只有 Git worktree 支持应用变更；非 Git 项目请使用恢复快照。");
                var source = (string)descriptor["repoRoot"];
                var worktree = (string)descriptor["worktreeRoot"];
                var changes = descriptor["changes"] as JArray ?? new JArray();
                var requested = new HashSet<string>((paths ?? new JArray()).Select(value => ((string)value ?? "").Trim()).Where(value => value.Length > 0), StringComparer.Ordinal);
                var selected = changes.OfType<JObject>().Where(change => requested.Count == 0 || requested.Contains((string)change["path"])).ToArray();
                if (selected.Length == 0) throw new InvalidOperationException("没有可应用的任务变更");
                if (requested.Count > 0 && selected.Select(change => (string)change["path"]).Distinct(StringComparer.Ordinal).Count() != requested.Count)
                    throw new InvalidOperationException("应用列表包含不属于当前任务的路径");

                var selector = string.Join(" ", selected.Select(ChangeSelector));
                var stageSelector = string.Join(" ", selected.Select(CurrentSelector));
                var patch = GitOutput(worktree, "diff --binary --full-index HEAD -- " + selector, 30000);
                var untracked = selected.Where(change => string.Equals((string)change["state"], "??", StringComparison.Ordinal)).Select(change => (string)change["path"]).ToArray();
                var copyPlan = new List<Tuple<string, string, string, long>>();
                foreach (var relative in untracked)
                {
                    var from = Inside(worktree, relative); var to = Inside(source, relative);
                    if (!File.Exists(from)) throw new FileNotFoundException("任务隔离区中的新增文件不存在", from);
                    if (File.Exists(to) || Directory.Exists(to)) throw new IOException("源工作区已存在同名路径，事务尚未开始：" + relative);
                    var info = new FileInfo(from);
                    copyPlan.Add(Tuple.Create(from, to, Sha256File(from), info.Length));
                }

                var journal = descriptor["applyJournal"] as JArray ?? new JArray();
                var sequence = journal.Count + 1;
                var baseCommitBefore = GitOutput(worktree, "rev-parse HEAD", 12000).Trim();
                var patchName = "apply-" + sequence.ToString("000") + ".patch";
                var patchPath = Path.Combine(AppPaths.Runs, Safe(jobId), patchName);
                if (patch.Length > 0)
                {
                    File.WriteAllText(patchPath, patch, new UTF8Encoding(false));
                    var check = Git(source, "apply --check --whitespace=nowarn " + Quote(patchPath), 60000);
                    if (check.ExitCode != 0) throw new InvalidOperationException("源工作区与任务基线存在冲突，事务尚未开始：" + NonEmpty(check.Error, check.Output));
                }

                var copied = new List<string>();
                var patchApplied = false;
                try
                {
                    if (patch.Length > 0)
                    {
                        var applied = Git(source, "apply --whitespace=nowarn " + Quote(patchPath), 60000);
                        if (applied.ExitCode != 0) throw new InvalidOperationException("无法把 Patch 应用到源工作区：" + NonEmpty(applied.Error, applied.Output));
                        patchApplied = true;
                    }
                    foreach (var item in copyPlan)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Item2)); File.Copy(item.Item1, item.Item2, false); copied.Add(item.Item2);
                    }

                    var add = Git(worktree, "add -A -- " + stageSelector, 30000);
                    if (add.ExitCode != 0) throw new InvalidOperationException("无法固化任务隔离基线：" + NonEmpty(add.Error, add.Output));
                    var commit = Git(worktree, "-c user.name=Claude-Workbench -c user.email=workbench@localhost commit --no-verify --no-gpg-sign --only -m " + Quote("Workbench apply " + jobId.Substring(0, Math.Min(8, jobId.Length))) + " -- " + selector, 60000);
                    if (commit.ExitCode != 0) throw new InvalidOperationException("无法提交任务隔离基线：" + NonEmpty(commit.Error, commit.Output));

                    var copiedEvidence = new JArray(copyPlan.Select(item => new JObject { ["path"] = Relative(source, item.Item2), ["sha256"] = item.Item3, ["bytes"] = item.Item4 }));
                    journal.Add(new JObject
                    {
                        ["sequence"] = sequence, ["patchFile"] = patch.Length > 0 ? patchName : "", ["patchSha256"] = patch.Length > 0 ? Sha256File(patchPath) : "",
                        ["paths"] = new JArray(selected.SelectMany(change => new[] { (string)change["path"], (string)change["originalPath"] }).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal)), ["copied"] = copiedEvidence,
                        ["baseCommitBefore"] = baseCommitBefore, ["worktreeCommit"] = GitOutput(worktree, "rev-parse HEAD", 12000).Trim(), ["appliedAt"] = ProviderStore.NowIso()
                    });
                    descriptor["applyJournal"] = journal;
                    descriptor["baseCommit"] = GitOutput(worktree, "rev-parse HEAD", 12000).Trim();
                    descriptor["state"] = GitChanges(worktree).Count > 0 ? "partial" : "applied";
                    descriptor["appliedAt"] = ProviderStore.NowIso();
                    Persist(jobId, descriptor);
                    var review = Describe(jobId);
                    return new JObject { ["ok"] = true, ["patchBytes"] = Encoding.UTF8.GetByteCount(patch), ["untrackedCopied"] = copiedEvidence, ["status"] = GitStatus(source), ["review"] = review };
                }
                catch
                {
                    Git(worktree, "reset -- " + selector, 12000);
                    foreach (var path in copied.AsEnumerable().Reverse()) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
                    if (patchApplied) Git(source, "apply --reverse --whitespace=nowarn " + Quote(patchPath), 60000);
                    throw;
                }
            }
        }

        public static JObject Revert(string jobId)
        {
            lock (Gate)
            {
                var descriptor = Describe(jobId);
                var kind = (string)descriptor["kind"];
                if (kind == "git-worktree")
                {
                    var journal = descriptor["applyJournal"] as JArray ?? new JArray();
                    if (journal.Count > 0) { RevertAppliedSource(jobId, descriptor, journal); DiscardGitChanges(descriptor); }
                    else DiscardGitChanges(descriptor);
                }
                else if (kind == "filesystem-snapshot") RestoreSnapshot(descriptor);
                else throw new InvalidOperationException("该任务没有可恢复的写入隔离层");
                descriptor["state"] = "reverted"; descriptor["revertedAt"] = ProviderStore.NowIso();
                Persist(jobId, descriptor);
                return new JObject { ["ok"] = true, ["state"] = "reverted", ["sourceReverted"] = kind == "git-worktree" && (descriptor["applyJournal"] as JArray ?? new JArray()).Count > 0, ["review"] = Describe(jobId) };
            }
        }

        public static JObject Discard(string jobId)
        {
            lock (Gate)
            {
                var descriptor = Describe(jobId);
                if ((string)descriptor["kind"] != "git-worktree") throw new InvalidOperationException("只有 Git worktree 支持丢弃隔离区变更");
                DiscardGitChanges(descriptor);
                descriptor["state"] = (descriptor["applyJournal"] as JArray ?? new JArray()).Count > 0 ? "applied" : "discarded";
                descriptor["discardedAt"] = ProviderStore.NowIso(); Persist(jobId, descriptor);
                return new JObject { ["ok"] = true, ["state"] = descriptor["state"], ["review"] = Describe(jobId) };
            }
        }

        private static JObject Snapshot(string source, string target)
        {
            Directory.CreateDirectory(target);
            long bytes = 0; var files = 0;
            var sourcePrefix = source.TrimEnd('\\') + "\\";
            var pending = new Stack<string>(); pending.Push(source);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var full = Path.GetFullPath(entry);
                    if (IsWorkspaceMetadata(source, full)) continue;
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
            foreach (var file in EnumerateWorkspaceFiles(source).ToArray())
            {
                var full = Path.GetFullPath(file);
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
            var paths = new HashSet<string>(Directory.Exists(source) ? EnumerateWorkspaceFiles(source).Select(path => Relative(source, path)) : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(snapshot)) foreach (var path in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)) paths.Add(Relative(snapshot, path));
            foreach (var relative in paths.Take(5000))
            {
                var current = Path.Combine(source, relative); var before = Path.Combine(snapshot, relative);
                var state = !File.Exists(before) ? "added" : !File.Exists(current) ? "deleted" : SameFile(before, current) ? "unchanged" : "modified";
                if (state != "unchanged") values.Add(new JObject { ["path"] = relative, ["state"] = state });
            }
            return values;
        }

        private static IEnumerable<string> EnumerateWorkspaceFiles(string root)
        {
            root = Path.GetFullPath(root);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var full = Path.GetFullPath(entry);
                    if (IsWorkspaceMetadata(root, full)) continue;
                    if (Directory.Exists(full)) pending.Push(full);
                    else yield return full;
                }
            }
        }

        private static bool IsWorkspaceMetadata(string root, string path)
        {
            var relative = Relative(root, path);
            return relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segment, ".claude-gui-v2", StringComparison.OrdinalIgnoreCase));
        }

        private static JArray GitChanges(string root)
        {
            var raw = GitOutput(root, "status --porcelain=v1 -z --untracked-files=all", 12000);
            var fields = raw.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            var changes = new JArray();
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                if (field.Length < 4) continue;
                var state = field.Substring(0, 2);
                var path = field.Substring(3).Replace('/', Path.DirectorySeparatorChar);
                var item = new JObject { ["state"] = state, ["path"] = path };
                if ((state.IndexOf('R') >= 0 || state.IndexOf('C') >= 0) && index + 1 < fields.Length)
                    item["originalPath"] = fields[++index].Replace('/', Path.DirectorySeparatorChar);
                changes.Add(item);
            }
            return changes;
        }

        private static void RevertAppliedSource(string jobId, JObject descriptor, JArray journal)
        {
            var source = (string)descriptor["repoRoot"];
            var worktree = (string)descriptor["worktreeRoot"];
            var entries = journal.OfType<JObject>().Reverse().ToArray();
            var currentHead = GitOutput(worktree, "rev-parse HEAD", 12000).Trim();
            var expectedHead = entries.Length == 0 ? "" : (string)entries[0]["worktreeCommit"] ?? "";
            var resetTarget = entries.Length == 0 ? "" : (string)entries[entries.Length - 1]["baseCommitBefore"] ?? "";
            if (expectedHead.Length == 0 || resetTarget.Length == 0 || !string.Equals(currentHead, expectedHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("任务隔离区在应用后又产生了后续提交，请先从最新 Review 依次撤销");
            var touchedPaths = entries.SelectMany(entry => (entry["paths"] as JArray ?? new JArray()).Values<string>())
                .Concat(entries.SelectMany(entry => (entry["copied"] as JArray ?? new JArray()).OfType<JObject>().Select(item => (string)item["path"])))
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var backupRoot = Path.Combine(AppPaths.Runs, Safe(jobId), "revert-backup-" + Guid.NewGuid().ToString("N"));
            var backup = BackupFiles(source, backupRoot, touchedPaths);
            try
            {
                foreach (var entry in entries)
                {
                    foreach (var copied in (entry["copied"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var target = Inside(source, (string)copied["path"]);
                        if (!File.Exists(target) || !string.Equals(Sha256File(target), (string)copied["sha256"], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("已应用的新增文件后来被修改，未执行撤销：" + (string)copied["path"]);
                    }
                    var patchName = (string)entry["patchFile"] ?? "";
                    if (patchName.Length == 0) continue;
                    var patchPath = Inside(Path.Combine(AppPaths.Runs, Safe(jobId)), patchName);
                    if (!File.Exists(patchPath) || !string.Equals(Sha256File(patchPath), (string)entry["patchSha256"], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("应用日志中的 Patch 已缺失或损坏，未执行撤销");
                }
                foreach (var entry in entries)
                {
                    var patchName = (string)entry["patchFile"] ?? "";
                    if (patchName.Length > 0)
                    {
                        var patchPath = Inside(Path.Combine(AppPaths.Runs, Safe(jobId)), patchName);
                        var check = Git(source, "apply --reverse --check --whitespace=nowarn " + Quote(patchPath), 60000);
                        if (check.ExitCode != 0) throw new InvalidOperationException("源工作区在应用后又发生变化，撤销事务已回滚：" + NonEmpty(check.Error, check.Output));
                        var reversed = Git(source, "apply --reverse --whitespace=nowarn " + Quote(patchPath), 60000);
                        if (reversed.ExitCode != 0) throw new InvalidOperationException("撤销 Patch 失败，撤销事务已回滚：" + NonEmpty(reversed.Error, reversed.Output));
                    }
                    foreach (var copied in (entry["copied"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var target = Inside(source, (string)copied["path"]); if (File.Exists(target)) File.Delete(target);
                    }
                }
                var reset = Git(worktree, "reset --hard " + Quote(resetTarget), 30000);
                if (reset.ExitCode != 0) throw new InvalidOperationException("任务隔离基线复原失败，撤销事务已回滚：" + NonEmpty(reset.Error, reset.Output));
            }
            catch { RestoreBackup(source, backupRoot, backup); throw; }
            finally { try { if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true); } catch { } }
        }

        private static JArray BackupFiles(string source, string backupRoot, IEnumerable<string> paths)
        {
            Directory.CreateDirectory(backupRoot); var result = new JArray();
            foreach (var relative in paths)
            {
                var file = Inside(source, relative); var exists = File.Exists(file);
                if (exists)
                {
                    var copy = Inside(backupRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)); File.Copy(file, copy, true);
                }
                result.Add(new JObject { ["path"] = relative, ["existed"] = exists });
            }
            return result;
        }

        private static void RestoreBackup(string source, string backupRoot, JArray backup)
        {
            foreach (var item in backup.OfType<JObject>())
            {
                var relative = (string)item["path"]; var target = Inside(source, relative);
                if ((bool?)item["existed"] ?? false)
                {
                    var copy = Inside(backupRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(copy, target, true);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
        }

        private static void DiscardGitChanges(JObject descriptor)
        {
            var root = (string)descriptor["worktreeRoot"];
            var restore = Git(root, "restore --staged --worktree .", 30000);
            if (restore.ExitCode != 0) throw new InvalidOperationException(NonEmpty(restore.Error, restore.Output));
            foreach (var change in GitChanges(root).OfType<JObject>().Where(item => string.Equals((string)item["state"], "??", StringComparison.Ordinal)))
            {
                var target = Inside(root, (string)change["path"]); if (File.Exists(target)) File.Delete(target);
            }
        }

        private static string ChangeSelector(JObject change)
        {
            var paths = new List<string>();
            var original = ((string)change["originalPath"] ?? "").Trim();
            var current = ((string)change["path"] ?? "").Trim();
            if (original.Length > 0) paths.Add(original);
            if (current.Length > 0 && !paths.Contains(current, StringComparer.Ordinal)) paths.Add(current);
            if (paths.Count == 0) throw new InvalidOperationException("任务变更路径为空");
            return string.Join(" ", paths.Select(path => Quote(":(literal)" + path.Replace(Path.DirectorySeparatorChar, '/'))));
        }

        private static string CurrentSelector(JObject change)
        {
            var current = ((string)change["path"] ?? "").Trim();
            if (current.Length == 0) throw new InvalidOperationException("任务变更路径为空");
            return Quote(":(literal)" + current.Replace(Path.DirectorySeparatorChar, '/'));
        }

        private static void Persist(string jobId, JObject descriptor)
        {
            var durable = (JObject)descriptor.DeepClone(); durable.Remove("changes");
            JsonUtil.WriteAtomic(Path.Combine(AppPaths.Runs, Safe(jobId), "isolation.json"), durable);
        }

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("X2")));
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
                File.WriteAllText(Path.Combine(gitRoot, "second 中文 file.txt"), "second-before", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(gitRoot, "rename old.txt"), "rename-content", new UTF8Encoding(false));
                Exec(gitRoot, "add ."); if (Exec(gitRoot, "commit -m initial").ExitCode != 0) return 52;
                var runId = "git-run"; var runDir = Path.Combine(AppPaths.Runs, runId); Directory.CreateDirectory(runDir);
                var isolated = TaskWorkspaceManager.Prepare(gitRoot, runId, runDir, true, "workspace-selftest-task");
                if (isolated.Kind != "git-worktree" || string.Equals(isolated.WorkerWorkspace, gitRoot, StringComparison.OrdinalIgnoreCase)) return 53;
                File.WriteAllText(Path.Combine(isolated.WorkerWorkspace, "tracked.txt"), "after", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(isolated.WorkerWorkspace, "second 中文 file.txt"), "second-after", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(isolated.WorkerWorkspace, "new file 中文.txt"), "new", new UTF8Encoding(false));
                if (Exec(isolated.WorkerWorkspace, "mv " + Q("rename old.txt") + " " + Q("rename 新.txt")).ExitCode != 0) return 54;
                var described = TaskWorkspaceManager.Describe(runId);
                if ((described["changes"] as JArray ?? new JArray()).Count < 4) return 55;
                if ((string)TaskWorkspaceManager.Diff(runId, "second 中文 file.txt")["diff"] == "") return 56;
                File.WriteAllText(Path.Combine(gitRoot, "new file 中文.txt"), "source-conflict", new UTF8Encoding(false));
                var conflictBlocked = false; try { TaskWorkspaceManager.Apply(runId, new JArray("tracked.txt", "new file 中文.txt")); } catch (IOException) { conflictBlocked = true; }
                if (!conflictBlocked || File.ReadAllText(Path.Combine(gitRoot, "tracked.txt"), Encoding.UTF8) != "before") return 57;
                File.Delete(Path.Combine(gitRoot, "new file 中文.txt"));
                var firstApply = TaskWorkspaceManager.Apply(runId, new JArray("tracked.txt", "new file 中文.txt"));
                if ((string)firstApply["review"]?["state"] != "partial" || File.ReadAllText(Path.Combine(gitRoot, "tracked.txt"), Encoding.UTF8) != "after" ||
                    !File.Exists(Path.Combine(gitRoot, "new file 中文.txt")) || File.ReadAllText(Path.Combine(gitRoot, "second 中文 file.txt"), Encoding.UTF8) != "second-before") return 58;
                File.WriteAllText(Path.Combine(isolated.WorkerWorkspace, "tracked.txt"), "after-second", new UTF8Encoding(false));
                var secondApply = TaskWorkspaceManager.Apply(runId, new JArray("tracked.txt"));
                if ((string)secondApply["review"]?["state"] != "partial" || File.ReadAllText(Path.Combine(gitRoot, "tracked.txt"), Encoding.UTF8) != "after-second") return 59;
                var finalApply = TaskWorkspaceManager.Apply(runId, new JArray("second 中文 file.txt", "rename 新.txt"));
                if ((string)finalApply["review"]?["state"] != "applied" || File.Exists(Path.Combine(gitRoot, "rename old.txt")) || !File.Exists(Path.Combine(gitRoot, "rename 新.txt"))) return 60;
                File.WriteAllText(Path.Combine(gitRoot, "tracked.txt"), "user-change-after-apply", new UTF8Encoding(false));
                var revertBlocked = false; try { TaskWorkspaceManager.Revert(runId); } catch (InvalidOperationException) { revertBlocked = true; }
                if (!revertBlocked || File.ReadAllText(Path.Combine(gitRoot, "tracked.txt"), Encoding.UTF8) != "user-change-after-apply" ||
                    File.Exists(Path.Combine(gitRoot, "rename old.txt")) || !File.Exists(Path.Combine(gitRoot, "rename 新.txt"))) return 61;
                File.WriteAllText(Path.Combine(gitRoot, "tracked.txt"), "after-second", new UTF8Encoding(false));
                TaskWorkspaceManager.Revert(runId);
                if (File.ReadAllText(Path.Combine(isolated.WorkerWorkspace, "tracked.txt"), Encoding.UTF8) != "before" ||
                    File.ReadAllText(Path.Combine(gitRoot, "tracked.txt"), Encoding.UTF8) != "before" || File.Exists(Path.Combine(gitRoot, "new file 中文.txt")) ||
                    !File.Exists(Path.Combine(gitRoot, "rename old.txt")) || File.Exists(Path.Combine(gitRoot, "rename 新.txt"))) return 62;

                var plainRoot = Path.Combine(root, "plain-source"); Directory.CreateDirectory(plainRoot);
                File.WriteAllText(Path.Combine(plainRoot, "plain.txt"), "plain-before", new UTF8Encoding(false));
                var oldStateRoot = Path.Combine(plainRoot, ".claude-gui-v2", "runs", new string('a', 80), "workspace-snapshot");
                Directory.CreateDirectory(oldStateRoot);
                var oldStateFile = Path.Combine(oldStateRoot, "state.txt");
                File.WriteAllText(oldStateFile, "state-before", new UTF8Encoding(false));
                var plainRun = "plain-run"; var plainRunDir = Path.Combine(AppPaths.Runs, plainRun); Directory.CreateDirectory(plainRunDir);
                TaskWorkspaceManager.Prepare(plainRoot, plainRun, plainRunDir, true, "plain-task");
                if (Directory.Exists(Path.Combine(plainRunDir, "workspace-snapshot", ".claude-gui-v2"))) return 63;
                File.WriteAllText(Path.Combine(plainRoot, "plain.txt"), "plain-after", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(plainRoot, "added.txt"), "added", new UTF8Encoding(false));
                File.WriteAllText(oldStateFile, "state-after", new UTF8Encoding(false));
                if ((TaskWorkspaceManager.Describe(plainRun)["changes"] as JArray ?? new JArray()).Count != 2) return 64;
                TaskWorkspaceManager.Revert(plainRun);
                if (File.ReadAllText(Path.Combine(plainRoot, "plain.txt"), Encoding.UTF8) != "plain-before" || File.Exists(Path.Combine(plainRoot, "added.txt")) ||
                    File.ReadAllText(oldStateFile, Encoding.UTF8) != "state-after") return 65;
                Exec(gitRoot, "worktree remove --force " + Q((string)isolated.Descriptor["worktreeRoot"]));
                return 0;
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(root, "workspace-selftest-error.txt"), error.ToString(), new UTF8Encoding(false)); } catch { }
                return 69;
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

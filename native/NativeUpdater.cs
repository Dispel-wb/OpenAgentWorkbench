using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace ClaudeCodeWorkbench
{
    internal static class NativeUpdater
    {
        public static int Apply(string stagedPath, string targetPath, string expectedSha256, bool launch)
        {
            try
            {
                stagedPath = Path.GetFullPath(stagedPath ?? "");
                targetPath = Path.GetFullPath(targetPath ?? "");
                if (!File.Exists(stagedPath)) throw new FileNotFoundException("Staged update does not exist", stagedPath);
                if (string.Equals(stagedPath, targetPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Staged and target paths must be different.");
                if (!string.Equals(Path.GetExtension(stagedPath), ".exe", StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Updater accepts EXE files only.");
                var actualHash = Sha256(stagedPath);
                if (!string.Equals(actualHash, (expectedSha256 ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Staged update SHA-256 mismatch.");
                WaitForTargetProcesses(targetPath, TimeSpan.FromSeconds(30));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                var next = targetPath + ".next";
                File.Copy(stagedPath, next, true);
                if (!string.Equals(Sha256(next), actualHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Copied update failed SHA-256 verification.");
                var previous = Path.Combine(Path.GetDirectoryName(targetPath), Path.GetFileNameWithoutExtension(targetPath) + ".previous.exe");
                if (File.Exists(targetPath)) File.Replace(next, targetPath, previous, true);
                else File.Move(next, targetPath);
                if (!string.Equals(Sha256(targetPath), actualHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed update failed SHA-256 verification.");
                if (launch)
                {
                    Process.Start(new ProcessStartInfo(targetPath, "--host") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppPaths.Workspace });
                    Thread.Sleep(500);
                    Process.Start(new ProcessStartInfo(targetPath, "--ui") { UseShellExecute = false, WorkingDirectory = AppPaths.Workspace });
                }
                return 0;
            }
            catch (Exception error)
            {
                try
                {
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), EditionInfo.StorageId);
                    Directory.CreateDirectory(root);
                    File.AppendAllText(Path.Combine(root, "update-error.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
                }
                catch { }
                return 1;
            }
        }

        public static string Sha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path)) return string.Concat(algorithm.ComputeHash(stream).Select(value => value.ToString("X2")));
        }

        private static void WaitForTargetProcesses(string targetPath, TimeSpan timeout)
        {
            var expires = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < expires)
            {
                var found = false;
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (!string.Equals(Path.GetFullPath(process.MainModule.FileName), targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        break;
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
                if (!found) return;
                Thread.Sleep(150);
            }
            throw new TimeoutException("Target application is still running; update was not applied.");
        }
    }
}

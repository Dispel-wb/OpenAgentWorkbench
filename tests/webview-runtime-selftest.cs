using System;
using System.IO;
using ClaudeCodeWorkbench;

internal static class WebViewRuntimeSelfTest
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    public static int Main(string[] args)
    {
        var variable = WebViewRuntimeInfo.RuntimeEnvironmentVariable;
        var original = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(Path.GetTempPath(), "Claude Workbench 中文 WebView " + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            Assert(WebViewRuntimeInfo.ResolveConfiguredFolder() == null, "Evergreen mode should not resolve a folder.");
            Assert(WebViewRuntimeInfo.RuntimeMode(null) == "evergreen", "Evergreen mode mismatch.");

            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "msedgewebview2.exe"), new byte[] { 0 });
            Environment.SetEnvironmentVariable(variable, root);
            Assert(string.Equals(WebViewRuntimeInfo.ResolveConfiguredFolder(), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "Fixed Runtime path mismatch.");
            Assert(WebViewRuntimeInfo.RuntimeMode(root) == "fixed", "Fixed mode mismatch.");

            Environment.SetEnvironmentVariable(variable, Path.Combine(root, "missing"));
            Assert(Throws<DirectoryNotFoundException>(() => WebViewRuntimeInfo.ResolveConfiguredFolder()), "Missing directory was accepted.");

            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            Environment.SetEnvironmentVariable(variable, empty);
            Assert(Throws<FileNotFoundException>(() => WebViewRuntimeInfo.ResolveConfiguredFolder()), "Directory without runtime executable was accepted.");

            Environment.SetEnvironmentVariable(variable, Path.Combine(root, "Edge", "Application", "123"));
            Assert(Throws<InvalidOperationException>(() => WebViewRuntimeInfo.ResolveConfiguredFolder()), "Unsupported Edge Application path was accepted.");

            Console.WriteLine("WebViewRuntimeSelfTest: PASS");
            Console.WriteLine("WindowsFamily: " + WebViewRuntimeInfo.WindowsFamily());
            Console.WriteLine("WindowsBuild: " + WebViewRuntimeInfo.WindowsBuild());
            return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}

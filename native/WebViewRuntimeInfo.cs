using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClaudeCodeWorkbench
{
    internal static class WebViewRuntimeInfo
    {
        internal const string RuntimeEnvironmentVariable = "CLAUDE_GUI_WEBVIEW2_RUNTIME";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            internal int Size;
            internal int Major;
            internal int Minor;
            internal int Build;
            internal int PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
            internal ushort ServicePackMajor;
            internal ushort ServicePackMinor;
            internal ushort SuiteMask;
            internal byte ProductType;
            internal byte Reserved;
        }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfo version);

        internal static string ResolveConfiguredFolder()
        {
            var configured = (Environment.GetEnvironmentVariable(RuntimeEnvironmentVariable) ?? "").Trim();
            if (configured.Length == 0) return null;

            var folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            if (folder.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WebView2 Fixed Runtime 不能位于网络或 UNC 路径。");
            if (folder.IndexOf(@"\Edge\Application\", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("WebView2 Fixed Runtime 不能直接使用 Edge\\Application 目录。");
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException("指定的 WebView2 Fixed Runtime 目录不存在：" + folder);
            if (!File.Exists(Path.Combine(folder, "msedgewebview2.exe")))
                throw new FileNotFoundException("WebView2 Fixed Runtime 目录中缺少 msedgewebview2.exe。", Path.Combine(folder, "msedgewebview2.exe"));
            return folder;
        }

        internal static string RuntimeMode(string configuredFolder)
        {
            return string.IsNullOrWhiteSpace(configuredFolder) ? "evergreen" : "fixed";
        }

        internal static string WindowsFamily()
        {
            int major;
            int build;
            GetWindowsVersion(out major, out build);
            if (major != 10) return "unsupported";
            return build >= 22000 ? "windows-11" : "windows-10";
        }

        internal static int WindowsBuild()
        {
            int major;
            int build;
            GetWindowsVersion(out major, out build);
            return build;
        }

        private static void GetWindowsVersion(out int major, out int build)
        {
            var native = new OsVersionInfo { Size = Marshal.SizeOf(typeof(OsVersionInfo)) };
            if (RtlGetVersion(ref native) == 0)
            {
                major = native.Major;
                build = native.Build;
                return;
            }
            major = Environment.OSVersion.Version.Major;
            build = Environment.OSVersion.Version.Build;
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCodeWorkbench
{
    internal static class PackageIdentity
    {
        private const int ErrorInsufficientBuffer = 122;
        private const int AppModelErrorNoPackage = 15700;
        private static readonly Lazy<bool> Packaged = new Lazy<bool>(Probe);

        public static bool IsPackaged { get { return Packaged.Value; } }

        internal static bool IsPackagedResult(int result, uint length)
        {
            return result == ErrorInsufficientBuffer && length > 0;
        }

        private static bool Probe()
        {
            try
            {
                uint length = 0;
                var result = GetCurrentPackageFullName(ref length, null);
                if (result == AppModelErrorNoPackage) return false;
                return IsPackagedResult(result, length);
            }
            catch { return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder packageFullName);
    }
}

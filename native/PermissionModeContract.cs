using System;

namespace ClaudeCodeWorkbench
{
    // One semantic contract shared by every supported CLI core.  The UI's Agent
    // mode means full-disk execution without approval; "full" is retained as a
    // legacy alias for saved settings created by older builds.
    internal static class PermissionModeContract
    {
        internal static string Normalize(string mode)
        {
            return (mode ?? "readonly").Trim().ToLowerInvariant();
        }

        internal static bool IsFullAccess(string mode)
        {
            mode = Normalize(mode);
            return mode == "agent" || mode == "full";
        }

        internal static bool IsReadOnly(string mode)
        {
            mode = Normalize(mode);
            return mode == "readonly" || mode == "plan";
        }

        internal static string CodexSandbox(string mode)
        {
            return IsFullAccess(mode) ? "danger-full-access" : IsReadOnly(mode) ? "read-only" : "workspace-write";
        }

        internal static string DshPermission(string mode)
        {
            return CodexSandbox(mode);
        }

        internal static bool PiUsesWorkspacePolicy(string mode)
        {
            return !IsFullAccess(mode);
        }
    }
}

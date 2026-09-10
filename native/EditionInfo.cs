namespace ClaudeCodeWorkbench
{
    internal static class EditionInfo
    {
#if OPEN_SOURCE
        public const bool IsOpenSource = true;
        public const string Id = "opensource";
        public const string ProductName = "Open Agent 中文工作台";
        public const string ProductId = "OpenAgentWorkbench.Native";
        public const string ExecutableName = "OpenAgentWorkbench.exe";
        public const string StorageId = "OpenAgentWorkbench";
        public const string ContractVersion = "0.7.0-preview.1";
        public const string DefaultWorkspace = @"D:\work\OpenAgent";
        public const string DefaultInstallDirectory = @"D:\softwares\OpenAgentWorkbench";
#else
        public const bool IsOpenSource = false;
        public const string Id = "local";
        public const string ProductName = "Claude Code 中文工作台";
        public const string ProductId = "ClaudeCodeWorkbench.Native";
        public const string ExecutableName = "ClaudeCodeWorkbench.exe";
        public const string StorageId = "ClaudeCodeWorkbench";
        public const string ContractVersion = "6.4.24-dev.native.local";
        public const string DefaultWorkspace = @"D:\work\Claude";
        public const string DefaultInstallDirectory = @"D:\softwares\ClaudeCode";
#endif
    }
}

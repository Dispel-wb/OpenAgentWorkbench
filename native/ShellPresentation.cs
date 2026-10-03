using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    // The desktop shell contract is owned by the native host. The WebView is a
    // rendering surface only; it does not decide the product information
    // architecture or the dimensions of the primary workspace regions.
    internal static class ShellPresentation
    {
        public static JObject Snapshot()
        {
            return new JObject
            {
                ["contract"] = "open-agent-native-shell-v1",
                ["implementation"] = "independent-csharp",
                ["layout"] = new JObject
                {
                    ["navigationWidth"] = 272,
                    ["contentWidth"] = 840,
                    ["workPanelWidth"] = 560,
                    ["compactBreakpoint"] = 920
                },
                ["commands"] = new JArray
                {
                    new JObject { ["id"] = "search", ["label"] = "搜索与命令", ["shortcut"] = "Ctrl K" },
                    new JObject { ["id"] = "new", ["label"] = "新建任务", ["shortcut"] = "Ctrl N" }
                },
                ["surfaces"] = new JArray("sessions", "conversation", "work-panel", "settings", "notifications"),
                ["provenance"] = new JObject
                {
                    ["sourceCodeReused"] = false,
                    ["thirdPartyAssetsReused"] = false,
                    ["designDocument"] = "docs/FRONTEND_ORIGIN.md"
                }
            };
        }
    }
}

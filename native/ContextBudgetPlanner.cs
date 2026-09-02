using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class PreparedContext
    {
        public string[] Attachments = new string[0];
        public string UserPrompt = "";
        public string AttachmentHint = "";
        public string SkillHint = "";
        public string TrustedInstructionText = "";
        public string MemoryText = "";
        public string SystemInstructionText = "";
        public JArray WorkspaceMemories = new JArray();
        public JArray TrustedInstructionSources = new JArray();
        public string Prompt = "";
        public JObject SkillCatalog = new JObject();
        public JObject Budget = new JObject();
        public JObject History = new JObject();
    }

    internal sealed class ContextPreparationException : Exception
    {
        public int StatusCode { get; private set; }
        public JObject Details { get; private set; }

        public ContextPreparationException(string message, int statusCode = 400, JObject details = null) : base(message)
        {
            StatusCode = statusCode;
            Details = details ?? new JObject();
        }

        public JObject Response()
        {
            var result = (JObject)Details.DeepClone();
            result["error"] = Message;
            return result;
        }
    }

    internal static class ContextBudgetPlanner
    {
        private const long DefaultContextWindow = 128000L;
        private static readonly object HistoryGate = new object();
        private static readonly Dictionary<string, HistoryCacheEntry> HistoryCache = new Dictionary<string, HistoryCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HistoryLocationCacheEntry> HistoryLocationCache = new Dictionary<string, HistoryLocationCacheEntry>(StringComparer.OrdinalIgnoreCase);

        public static PreparedContext Prepare(JObject payload, JObject provider, string model, string workspace, JArray workspaceMemories = null)
        {
            var attachments = ResolveAttachments(payload);
            ValidateVisualTransport(provider, model, attachments);

            var userPrompt = (string)payload["prompt"] ?? "";
            var attachmentHint = BuildAttachmentHint(attachments);
            var promptBeforeSkills = userPrompt + attachmentHint;
            var skillCatalog = SkillCatalog.Build(workspace, promptBeforeSkills);
            var skillHint = SkillCatalog.RoutingHint(skillCatalog);
            var prompt = promptBeforeSkills + skillHint;
            var trustedRuntime = ExtensionTrustPolicy.TrustedRuntimeInputs(workspace);
            var trustedInstructionText = (string)trustedRuntime["instructionText"] ?? "";
            var memories = workspaceMemories == null ? new JArray() : new JArray(workspaceMemories.OfType<JObject>()
                .Where(item => (bool?)item["active"] ?? false).Select(item => item.DeepClone()));
            var memoryText = BuildMemoryText(memories);
            var harness = AgentWorkerSdk.SelectedHarness(payload);
            var instructions = string.Join("\n\n", new[] { trustedInstructionText, memoryText }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if ((harness == "codex" || harness == "dsh") && instructions.Length > 0)
                prompt = "工作台中已信任的项目说明与启用的长期记忆（不改变核心权限策略）：\n" + instructions + "\n\n本轮用户要求：\n" + prompt;
            var resume = (bool?)payload["resume"] ?? false;
            var sessionId = ((string)payload["claudeSessionId"] ?? (string)payload["sessionId"] ?? "").Trim();
            var history = harness == "claude" ? MeasureHistory(workspace, sessionId, resume)
                : new JObject { ["measured"] = false, ["evidence"] = "selected-cli-history-unavailable" };
            var budget = Plan(provider, model, userPrompt, attachmentHint, skillHint, resume, history, trustedInstructionText, memoryText);

            return new PreparedContext
            {
                Attachments = attachments,
                UserPrompt = userPrompt,
                AttachmentHint = attachmentHint,
                SkillHint = skillHint,
                TrustedInstructionText = trustedInstructionText,
                MemoryText = memoryText,
                SystemInstructionText = instructions,
                WorkspaceMemories = memories,
                TrustedInstructionSources = trustedRuntime["instructionSources"] as JArray ?? new JArray(),
                Prompt = prompt,
                SkillCatalog = skillCatalog,
                Budget = budget,
                History = history
            };
        }

        public static JObject Plan(JObject provider, string model, string userPrompt, string attachmentHint, string skillHint, bool resumeRequested, JObject history = null, string trustedInstructionText = "", string memoryText = "")
        {
            string evidence;
            var modelCapability = provider?["capabilities"]?["models"]?[model] as JObject;
            var contextWindow = ResolveContextWindow(modelCapability, model, out evidence);
            var outputReserve = FirstPositive(modelCapability, "maxOutputTokens", "max_output_tokens", "outputLimit", "output_limit");
            if (outputReserve <= 0) outputReserve = Math.Max(2048L, Math.Min(8192L, contextWindow / 8L));
            outputReserve = Math.Min(outputReserve, Math.Max(2048L, contextWindow / 3L));
            var runtimeReserve = Math.Max(4000L, Math.Min(16000L, contextWindow / 8L));
            var inputBudget = Math.Max(1024L, contextWindow - outputReserve - runtimeReserve);

            var userTokens = EstimateTokens(userPrompt);
            var attachmentTokens = EstimateTokens(attachmentHint);
            var skillTokens = EstimateTokens(skillHint);
            var trustedInstructionTokens = EstimateTokens(trustedInstructionText);
            var memoryTokens = EstimateTokens(memoryText);
            var currentTurnTokens = userTokens + attachmentTokens + skillTokens + trustedInstructionTokens + memoryTokens;
            var historyMeasured = resumeRequested && ((bool?)history?["measured"] ?? false);
            var coreOwnedHistory = (string)history?["evidence"] == "selected-cli-history-unavailable";
            var historyTokens = historyMeasured ? Math.Max(0L, (long?)history?["estimatedTokens"] ?? 0L) : 0L;
            var knownTokens = currentTurnTokens + historyTokens;
            var projectedTokens = knownTokens + runtimeReserve;
            var ratio = inputBudget <= 0 ? 1D : Math.Min(9D, knownTokens / (double)inputBudget);
            var reliableCapacity = evidence == "provider-metadata" || evidence == "model-name";
            var decision = "pass";
            var warnings = new JArray();

            if (currentTurnTokens > inputBudget)
            {
                decision = reliableCapacity ? "blocked" : "warning";
                warnings.Add(reliableCapacity
                    ? "本轮已知 Context 超过模型可用输入预算，发送会被阻止。请缩短要求、减少引用或选择更大 Context Window 的模型。"
                    : "按保守默认窗口估算，本轮 Context 可能超限；服务商未返回可靠的 Context Window 元数据。");
            }
            else if (historyMeasured && knownTokens > inputBudget)
            {
                decision = "warning";
                warnings.Add("上一次 Provider usage 与本轮输入合计已超过当前输入预算；Claude Code 原生 autocompact 会在运行时决定压缩，工作台不会仅凭历史快照提前拒绝。");
            }
            else if (ratio >= 0.85D)
            {
                decision = "warning";
                warnings.Add("本轮已知 Context 已接近可用输入预算，工具与系统上下文可能触发服务商侧压缩或拒绝。");
            }

            if (resumeRequested)
            {
                if (!historyMeasured)
                {
                    if (decision == "pass") decision = "unknown";
                    warnings.Add(coreOwnedHistory ? "所选 CLI 自主管理会话历史，工作台未量测其完整上下文；以核心实际报告的 usage 为准。" : "续接会话存在 transcript，但没有可用的 Provider usage 快照；运行后以服务商实际 usage 为准。");
                }
                else
                {
                    warnings.Add("会话历史量来自 transcript 中最近一次 Provider usage 快照，包含缓存输入与上一轮输出；它用于风险提示，不等同于下一次账单。");
                }
            }
            if (evidence == "fallback") warnings.Add("当前模型没有可验证的 Context Window；128K 仅用于本地风险提示，不作为硬限制。");

            var sources = new JArray(
                Source("user", "用户要求", userTokens),
                Source("attachment-reference", "附件路径引用", attachmentTokens),
                Source("skill-metadata", "按需 Skill 元数据", skillTokens),
                Source("trusted-project-instructions", "已信任的项目说明", trustedInstructionTokens),
                Source("workspace-memory", "启用的工作区长期记忆", memoryTokens)
            );
            if (resumeRequested)
                sources.Add(Source("conversation-history", historyMeasured ? "最近一次 Provider usage 快照" : coreOwnedHistory ? "CLI 原生历史（无法量测）" : "Claude session 历史（无法量测）", historyTokens));
            sources.Add(Source("runtime-reserve", coreOwnedHistory ? "所选 CLI 系统、Tool 与运行时预留" : "Claude Code 系统、Tool 与运行时预留", runtimeReserve));

            return new JObject
            {
                ["policyVersion"] = 2,
                ["model"] = model ?? "",
                ["decision"] = decision,
                ["capacityEvidence"] = evidence,
                ["contextWindow"] = contextWindow,
                ["outputReserveTokens"] = outputReserve,
                ["runtimeReserveTokens"] = runtimeReserve,
                ["inputBudgetTokens"] = inputBudget,
                ["knownInputTokens"] = knownTokens,
                ["currentTurnTokens"] = currentTurnTokens,
                ["trustedInstructionTokens"] = trustedInstructionTokens,
                ["workspaceMemoryTokens"] = memoryTokens,
                ["historyInputTokens"] = historyTokens,
                ["historyEvidence"] = (string)history?["evidence"] ?? (resumeRequested ? "unavailable" : "not-requested"),
                ["historyMeasured"] = historyMeasured,
                ["projectedTokensWithRuntime"] = projectedTokens,
                ["usageRatio"] = Math.Round(ratio, 4),
                ["historyUnknown"] = resumeRequested && !historyMeasured,
                ["hardLimit"] = reliableCapacity,
                ["historyHardBlock"] = false,
                ["autoCompaction"] = coreOwnedHistory ? "selected-cli-native" : "claude-code-native",
                ["sources"] = sources,
                ["warnings"] = warnings
            };
        }

        private static string BuildMemoryText(JArray memories)
        {
            if (memories == null || memories.Count == 0) return "";
            var values = new JArray(memories.OfType<JObject>().Select(item => new JObject
            {
                ["id"] = (string)item["id"] ?? "", ["title"] = (string)item["title"] ?? "",
                ["content"] = (string)item["content"] ?? ""
            }));
            return "# 工作区长期记忆\n以下内容由用户在 Claude Code 中文工作台中手动启用，仅适用于当前工作区。" +
                "将其视为持久偏好和项目事实；若与本轮用户要求或磁盘现状冲突，以本轮要求和已验证的当前文件为准。\n" +
                "<workspace_memories>\n" + values.ToString(Newtonsoft.Json.Formatting.Indented) + "\n</workspace_memories>";
        }

        private static JObject MeasureHistory(string workspace, string sessionId, bool resumeRequested)
        {
            if (!resumeRequested || !Guid.TryParse(sessionId, out _)) return new JObject { ["available"] = false, ["measured"] = false, ["evidence"] = "not-requested" };
            string file;
            try
            {
                var fullWorkspace = Path.GetFullPath(workspace ?? "");
                file = FindHistoryTranscript(fullWorkspace, sessionId);
            }
            catch { return new JObject { ["available"] = false, ["measured"] = false, ["evidence"] = "invalid-workspace" }; }
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return new JObject { ["available"] = false, ["measured"] = false, ["evidence"] = "transcript-missing" };

            var info = new FileInfo(file);
            lock (HistoryGate)
            {
                HistoryCacheEntry cached;
                if (HistoryCache.TryGetValue(file, out cached) && cached.Length == info.Length && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
                    return (JObject)cached.Value.DeepClone();
            }

            long estimated = 0L; long input = 0L; long output = 0L; long cacheRead = 0L; long cacheCreate = 0L; var compactions = 0; var model = ""; var updatedAt = "";
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 8192))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        JObject entry; try { entry = JObject.Parse(line); } catch { continue; }
                        var signal = string.Join(" ", new[] { (string)entry["type"], (string)entry["subtype"], (string)entry["status"], (string)entry["event"]?["type"] });
                        if (signal.IndexOf("compact", StringComparison.OrdinalIgnoreCase) >= 0) compactions++;
                        var usage = entry["message"]?["usage"] as JObject;
                        if (usage == null) continue;
                        var nextInput = Math.Max(0L, (long?)usage["input_tokens"] ?? 0L);
                        var nextOutput = Math.Max(0L, (long?)usage["output_tokens"] ?? 0L);
                        var nextCacheRead = Math.Max(0L, (long?)usage["cache_read_input_tokens"] ?? 0L);
                        var nextCacheCreate = Math.Max(0L, (long?)usage["cache_creation_input_tokens"] ?? 0L);
                        var nextEstimate = nextInput + nextOutput + nextCacheRead + nextCacheCreate;
                        if (nextEstimate <= 0) continue;
                        input = nextInput; output = nextOutput; cacheRead = nextCacheRead; cacheCreate = nextCacheCreate; estimated = nextEstimate;
                        model = (string)entry["message"]?["model"] ?? model; updatedAt = (string)entry["timestamp"] ?? updatedAt;
                    }
                }
            }
            catch { return new JObject { ["available"] = true, ["measured"] = false, ["evidence"] = "transcript-read-failed", ["transcriptBytes"] = info.Length }; }

            var result = new JObject
            {
                ["available"] = true, ["measured"] = estimated > 0L,
                ["evidence"] = estimated > 0L ? "transcript-provider-usage" : "transcript-no-usage",
                ["estimatedTokens"] = estimated, ["inputTokens"] = input, ["outputTokens"] = output,
                ["cacheReadInputTokens"] = cacheRead, ["cacheCreationInputTokens"] = cacheCreate,
                ["model"] = model, ["compactions"] = compactions, ["updatedAt"] = updatedAt, ["transcriptBytes"] = info.Length
            };
            lock (HistoryGate)
            {
                if (HistoryCache.Count >= 256) HistoryCache.Remove(HistoryCache.Keys.First());
                HistoryCache[file] = new HistoryCacheEntry { Length = info.Length, LastWriteTicks = info.LastWriteTimeUtc.Ticks, Value = (JObject)result.DeepClone() };
            }
            return result;
        }

        private static string FindHistoryTranscript(string workspace, string sessionId)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (!Directory.Exists(root)) return "";
            var cacheKey = workspace + "|" + sessionId;
            lock (HistoryGate)
            {
                HistoryLocationCacheEntry cached;
                if (HistoryLocationCache.TryGetValue(cacheKey, out cached) && cached.ExpiresAtUtc > DateTime.UtcNow && File.Exists(cached.Path)) return cached.Path;
            }
            var projectKey = new string(workspace.Select(character => char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '-').ToArray());
            var exact = Path.Combine(root, projectKey, sessionId + ".jsonl");
            string[] candidates;
            try { candidates = Directory.GetFiles(root, sessionId + ".jsonl", SearchOption.AllDirectories); }
            catch { candidates = File.Exists(exact) ? new[] { exact } : new string[0]; }
            var selected = candidates.Where(file =>
            {
                if (string.Equals(file, exact, StringComparison.OrdinalIgnoreCase)) return true;
                var descriptor = JsonUtil.Read(file + ".workbench.json", new JObject()) as JObject ?? new JObject();
                var source = ((string)descriptor["sourceWorkspace"] ?? "").Trim();
                if (source.Length > 0)
                {
                    try { return string.Equals(Path.GetFullPath(source), workspace, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }
                try
                {
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true, 8192))
                    {
                        for (var index = 0; index < 80; index++)
                        {
                            var line = reader.ReadLine();
                            if (line == null) break;
                            JObject entry;
                            try { entry = JObject.Parse(line); } catch { continue; }
                            var value = ((string)entry["workbenchSourceCwd"] ?? (string)entry["cwd"] ?? "").Trim();
                            if (value.Length == 0) continue;
                            try { return string.Equals(Path.GetFullPath(value), workspace, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        }
                    }
                }
                catch { }
                return false;
            }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
            if (selected.Length > 0)
            {
                lock (HistoryGate)
                {
                    if (HistoryLocationCache.Count >= 256) HistoryLocationCache.Remove(HistoryLocationCache.Keys.First());
                    HistoryLocationCache[cacheKey] = new HistoryLocationCacheEntry { Path = selected, ExpiresAtUtc = DateTime.UtcNow.AddSeconds(2) };
                }
            }
            return selected;
        }

        private sealed class HistoryCacheEntry
        {
            public long Length;
            public long LastWriteTicks;
            public JObject Value;
        }

        private sealed class HistoryLocationCacheEntry
        {
            public string Path;
            public DateTime ExpiresAtUtc;
        }

        public static long EstimateTokens(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0L;
            long wide = value.Count(character => character > 255);
            return Math.Max(1L, (value.Length - wide + 3L) / 4L + wide);
        }

        private static JObject Source(string type, string label, long tokens)
        {
            return new JObject { ["type"] = type, ["label"] = label, ["estimatedTokens"] = tokens };
        }

        private static string[] ResolveAttachments(JObject payload)
        {
            try
            {
                var attachments = (payload["attachments"] as JArray ?? new JArray())
                    .Select(value => ((string)value ?? "").Trim()).Where(value => value.Length > 0)
                    .Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var path in attachments)
                {
                    if (!System.IO.File.Exists(path)) throw new InvalidOperationException("附件不存在或已被移动：" + path);
                    using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                        System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete)) { }
                }
                return attachments;
            }
            catch (Exception error)
            {
                throw new ContextPreparationException("附件无法读取：" + error.Message);
            }
        }

        private static void ValidateVisualTransport(JObject provider, string model, string[] attachments)
        {
            var visual = new HashSet<string>(new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".pdf" }, StringComparer.OrdinalIgnoreCase);
            if (!attachments.Any(path => visual.Contains(System.IO.Path.GetExtension(path)))) return;
            var protocol = (string)provider?["text"]?["protocol"] ?? "";
            var modelCapability = provider?["capabilities"]?["models"]?[model] as JObject;
            if (string.Equals(protocol, "openai", StringComparison.OrdinalIgnoreCase))
                throw new ContextPreparationException(
                    "当前 OpenAI 兼容转换器只传递文字，不能可靠传输图片或 PDF；请切换到已验证的 Anthropic Vision 模型", 400,
                    new JObject { ["capability"] = "vision", ["evidence"] = "adapter-transport" });
            if (modelCapability != null && modelCapability["vision"]?.Type == JTokenType.Boolean && !(bool)modelCapability["vision"])
                throw new ContextPreparationException(
                    "模型接口元数据明确表示当前模型不支持图片输入", 400,
                    new JObject { ["capability"] = "vision", ["evidence"] = modelCapability["evidence"] });
        }

        private static string BuildAttachmentHint(string[] attachments)
        {
            if (attachments.Length == 0) return "";
            return "\n\n用户已明确引用以下本地文件。列表中的每一项都是原始文件的绝对路径，不是上传后的副本；请直接从该路径读取。文本、代码、图片或 PDF 优先使用 Read，Office 等格式请使用可用的 Skill 或本地工具解析。不得假装已经读取；若读取失败，请指出具体文件与原因。\n<attached_files>\n" +
                new JArray(attachments).ToString(Newtonsoft.Json.Formatting.Indented) + "\n</attached_files>";
        }

        private static long ResolveContextWindow(JObject capability, string model, out string evidence)
        {
            var configured = FirstPositive(capability, "contextWindow", "context_window", "contextLength", "context_length", "maxContextTokens", "max_input_tokens", "inputLimit");
            if (configured > 0)
            {
                evidence = "provider-metadata";
                return Math.Max(4096L, configured);
            }

            var normalized = (model ?? "").ToLowerInvariant();
            var bracket = Regex.Match(normalized, @"\[(\d+)m\]");
            if (bracket.Success && long.TryParse(bracket.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var millions))
            {
                evidence = "model-name";
                return Math.Max(4096L, millions * 1000000L);
            }
            if (Regex.IsMatch(normalized, @"(^|[-_ ])1m($|[-_ ])") || normalized.Contains("million"))
            {
                evidence = "model-name";
                return 1000000L;
            }
            if (normalized.Contains("claude")) { evidence = "model-name"; return 200000L; }
            if (normalized.Contains("deepseek")) { evidence = "model-name"; return 128000L; }
            if (normalized.Contains("gemini") || normalized.Contains("gpt-4.1") || normalized.Contains("gpt-5"))
            {
                evidence = "model-name";
                return 1000000L;
            }
            evidence = "fallback";
            return DefaultContextWindow;
        }

        private static long FirstPositive(JObject source, params string[] names)
        {
            if (source == null) return 0L;
            foreach (var name in names)
            {
                var token = source[name];
                if (token == null) continue;
                if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                {
                    var value = (long)token;
                    if (value > 0) return value;
                }
                long parsed;
                if (long.TryParse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0) return parsed;
            }
            return 0L;
        }
    }
}

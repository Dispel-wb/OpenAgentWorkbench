using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class ToolRuntimeSettings
    {
        public int MaxInputBytes { get; private set; }
        public int MaxOutputBytes { get; private set; }
        public int MaxErrorBytes { get; private set; }
        public int MaxDurationSeconds { get; private set; }
        public int ApprovalTimeoutSeconds { get; private set; }
        public string TimeoutAction { get; private set; }

        public static ToolRuntimeSettings From(JObject value)
        {
            value = value ?? new JObject();
            return new ToolRuntimeSettings
            {
                MaxInputBytes = Clamp((int?)value["maxInputBytes"] ?? 65536, 4096, 1048576),
                MaxOutputBytes = Clamp((int?)value["maxOutputBytes"] ?? 524288, 16384, 4194304),
                MaxErrorBytes = Clamp((int?)value["maxErrorBytes"] ?? 32768, 4096, 262144),
                MaxDurationSeconds = Clamp((int?)value["maxDurationSeconds"] ?? 1800, 1, 86400),
                ApprovalTimeoutSeconds = Clamp((int?)value["approvalTimeoutSeconds"] ?? 600, 5, 3600),
                TimeoutAction = "cancel_run"
            };
        }

        public JObject Manifest()
        {
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["maxInputBytes"] = MaxInputBytes,
                ["maxOutputBytes"] = MaxOutputBytes,
                ["maxErrorBytes"] = MaxErrorBytes,
                ["maxDurationSeconds"] = MaxDurationSeconds,
                ["approvalTimeoutSeconds"] = ApprovalTimeoutSeconds,
                ["timeoutAction"] = TimeoutAction,
                ["redactionEnabled"] = true,
                ["persistence"] = "redacted_truncated",
                ["countedInPrompt"] = false
            };
        }

        private static int Clamp(int value, int minimum, int maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
    }

    internal sealed class PersistedToolValue
    {
        public JToken Token { get; set; }
        public string Json { get; set; }
        public JObject Metadata { get; set; }
    }

    internal static class ToolRuntimePolicy
    {
        private const string Redacted = "[REDACTED]";
        private static readonly Regex SensitiveName = new Regex(
            "(^|[_\\-.])(authorization|api[_-]?key|token|secret|password|passwd|cookie|credential|private[_-]?key)($|[_\\-.])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex BearerSecret = new Regex("(?i)\\bBearer\\s+[A-Za-z0-9._~+\\-/=]{8,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SkSecret = new Regex("(?i)\\bsk-[A-Za-z0-9_-]{12,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex QuerySecret = new Regex("(?i)([?&](?:api[_-]?key|token|secret|password)=)[^&#\\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static JObject DefaultManifest() { return ToolRuntimeSettings.From(null).Manifest(); }

        public static PersistedToolValue Prepare(JToken source, int maxBytes, bool input)
        {
            var raw = source == null ? JValue.CreateNull() : source.DeepClone();
            var originalJson = raw.ToString(Formatting.None);
            var originalBytes = Encoding.UTF8.GetByteCount(originalJson);
            var redacted = false;
            var safe = RedactToken(raw, ref redacted);
            var safeJson = safe.ToString(Formatting.None);
            var safeBytes = Encoding.UTF8.GetByteCount(safeJson);
            var truncated = safeBytes > maxBytes;
            if (truncated)
            {
                var previewBudget = Math.Max(1024, maxBytes - 320);
                var preview = ClipUtf8(safeJson, previewBudget);
                safe = input
                    ? (JToken)new JObject
                    {
                        ["_workbenchTruncated"] = true,
                        ["originalBytes"] = originalBytes,
                        ["preview"] = preview
                    }
                    : JValue.CreateString(preview + "\n\n[Tool output truncated by Workbench]");
                safeJson = safe.ToString(Formatting.None);
            }
            return new PersistedToolValue
            {
                Token = safe,
                Json = safeJson,
                Metadata = new JObject
                {
                    ["originalBytes"] = originalBytes,
                    ["persistedBytes"] = Encoding.UTF8.GetByteCount(safeJson),
                    ["truncated"] = truncated,
                    ["redacted"] = redacted
                }
            };
        }

        public static string SanitizeWorkerEvent(string payload, JObject manifest)
        {
            JObject value;
            try { value = JObject.Parse(payload); } catch { return RedactAndLimit(payload, ToolRuntimeSettings.From(manifest).MaxOutputBytes, out _); }
            var settings = ToolRuntimeSettings.From(manifest);
            var blocks = value["message"]?["content"] as JArray ?? new JArray();
            foreach (var block in blocks.OfType<JObject>())
            {
                var type = (string)block["type"] ?? "";
                if (type == "tool_use") block["input"] = Prepare(block["input"] ?? new JObject(), settings.MaxInputBytes, true).Token;
                else if (type == "tool_result") block["content"] = Prepare(block["content"] ?? JValue.CreateString(""), settings.MaxOutputBytes, false).Token;
            }
            return value.ToString(Formatting.None);
        }

        public static string RedactAndLimit(string value, int maxBytes, out bool redacted)
        {
            var safe = RedactText(value ?? "", out redacted);
            return Encoding.UTF8.GetByteCount(safe) <= maxBytes ? safe : ClipUtf8(safe, maxBytes);
        }

        private static JToken RedactToken(JToken token, ref bool redacted)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var output = new JObject();
                foreach (var property in obj.Properties())
                {
                    if (SensitiveName.IsMatch(property.Name)) { output[property.Name] = Redacted; redacted = true; }
                    else output[property.Name] = RedactToken(property.Value, ref redacted);
                }
                return output;
            }
            var array = token as JArray;
            if (array != null)
            {
                var output = new JArray();
                foreach (var item in array) output.Add(RedactToken(item, ref redacted));
                return output;
            }
            if (token.Type != JTokenType.String) return token.DeepClone();
            bool changed;
            var text = RedactText((string)token ?? "", out changed);
            redacted |= changed;
            return JValue.CreateString(text);
        }

        private static string RedactText(string value, out bool redacted)
        {
            var safe = BearerSecret.Replace(value ?? "", "Bearer " + Redacted);
            safe = SkSecret.Replace(safe, Redacted);
            safe = QuerySecret.Replace(safe, match => match.Groups[1].Value + Redacted);
            redacted = !string.Equals(safe, value ?? "", StringComparison.Ordinal);
            return safe;
        }

        private static string ClipUtf8(string value, int maxBytes)
        {
            value = value ?? "";
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
            const string marker = "\n...[truncated]...\n";
            var markerBytes = Encoding.UTF8.GetByteCount(marker);
            var available = Math.Max(0, maxBytes - markerBytes);
            var head = TakePrefix(value, available * 3 / 4);
            var tail = TakeSuffix(value, available - Encoding.UTF8.GetByteCount(head));
            return head + marker + tail;
        }

        private static string TakePrefix(string value, int maxBytes)
        {
            var low = 0; var high = value.Length;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (middle < value.Length && char.IsHighSurrogate(value[middle - 1])) middle--;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, middle)) <= maxBytes) low = Math.Max(low + 1, middle);
                else high = middle - 1;
            }
            var length = Math.Min(low, value.Length);
            if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, Math.Max(0, length));
        }

        private static string TakeSuffix(string value, int maxBytes)
        {
            var low = 0; var high = value.Length;
            while (low < high)
            {
                var length = (low + high + 1) / 2;
                var start = value.Length - length;
                if (start > 0 && char.IsLowSurrogate(value[start])) { length--; start++; }
                if (Encoding.UTF8.GetByteCount(value.Substring(start, length)) <= maxBytes) low = Math.Max(low + 1, length);
                else high = length - 1;
            }
            var finalLength = Math.Min(low, value.Length);
            var finalStart = value.Length - finalLength;
            if (finalStart > 0 && finalStart < value.Length && char.IsLowSurrogate(value[finalStart])) { finalStart++; finalLength--; }
            return value.Substring(finalStart, Math.Max(0, finalLength));
        }
    }
}

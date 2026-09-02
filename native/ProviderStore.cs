using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    internal sealed class ProviderStore
    {
        private readonly object _gate = new object();
        private readonly string _path;
        private JArray _providers;

        public ProviderStore()
        {
            _path = Path.Combine(AppPaths.Data, "providers-v2.json");
            Load();
        }

        public JArray AllPublic()
        {
            lock (_gate) return new JArray(_providers.OfType<JObject>().Select(Public));
        }

        public JObject Get(string id)
        {
            lock (_gate)
            {
                var item = _providers.OfType<JObject>().FirstOrDefault(value => (string)value["id"] == id);
                return item == null ? null : (JObject)item.DeepClone();
            }
        }

        public string Token(string id)
        {
            var item = Get(id);
            if (item == null) throw new KeyNotFoundException("API 配置不存在");
            return SecretStore.Unprotect((string)item["tokenEncrypted"] ?? "");
        }

        public JObject Upsert(JObject payload)
        {
            lock (_gate)
            {
                var id = ((string)payload["id"] ?? "").Trim();
                if (id.Length == 0) id = Guid.NewGuid().ToString();
                var existing = _providers.OfType<JObject>().FirstOrDefault(value => (string)value["id"] == id);
                var token = ((string)payload["token"] ?? "").Trim();
                var encrypted = token.Length > 0 ? SecretStore.Protect(token) : (string)(existing == null ? null : existing["tokenEncrypted"]);
                if (string.IsNullOrWhiteSpace(encrypted)) throw new InvalidOperationException("API 令牌不能为空");

                var text = Capability(payload["text"] as JObject, "anthropic");
                var image = Capability(payload["image"] as JObject, "openai-images");
                if (!(bool)text["enabled"] && !(bool)image["enabled"])
                    throw new InvalidOperationException("至少启用文字工作或图像生成中的一项");
                if ((bool)text["enabled"] && string.IsNullOrWhiteSpace((string)text["baseUrl"]))
                    throw new InvalidOperationException("已启用文字工作，但没有填写文字接口地址");
                if ((bool)image["enabled"] && string.IsNullOrWhiteSpace((string)image["baseUrl"]))
                    throw new InvalidOperationException("已启用图像生成，但没有填写生图接口地址");
                if ((bool)text["enabled"]) ValidateEndpoint((string)text["baseUrl"], "文字接口地址");
                if ((bool)image["enabled"]) ValidateEndpoint((string)image["baseUrl"], "生图接口地址");
                var authStyle = (((string)payload["authStyle"] ?? "auto").Trim().ToLowerInvariant());
                if (!new[] { "auto", "bearer", "x-api-key" }.Contains(authStyle))
                    throw new InvalidOperationException("鉴权方式只支持自动尝试、Bearer Token 或 x-api-key");
                if (!new[] { "anthropic", "openai" }.Contains((string)text["protocol"]))
                    throw new InvalidOperationException("文字协议只支持 Anthropic-compatible 或 OpenAI-compatible");
                if (!string.Equals((string)image["protocol"], "openai-images", StringComparison.Ordinal))
                    throw new InvalidOperationException("生图协议只支持 OpenAI Images-compatible");

                var item = new JObject
                {
                    ["id"] = id,
                    ["name"] = (((string)payload["name"] ?? "未命名 API").Trim()),
                    ["tokenEncrypted"] = encrypted,
                    ["authStyle"] = authStyle,
                    ["text"] = text,
                    ["image"] = image,
                    ["capabilities"] = payload["capabilities"] == null
                        ? (existing == null || existing["capabilities"] == null ? new JObject { ["schemaVersion"] = 2, ["models"] = new JObject(), ["evidencePolicy"] = "unknown-until-probed" } : existing["capabilities"].DeepClone())
                        : payload["capabilities"].DeepClone(),
                    ["createdAt"] = existing == null ? NowIso() : ((string)existing["createdAt"] ?? NowIso()),
                    ["updatedAt"] = NowIso()
                };
                if (existing == null) _providers.Add(item);
                else existing.Replace(item);
                Save();
                return Public(item);
            }
        }

        public bool Delete(string id)
        {
            lock (_gate)
            {
                var item = _providers.OfType<JObject>().FirstOrDefault(value => (string)value["id"] == id);
                if (item == null) return false;
                item.Remove();
                Save();
                return true;
            }
        }

        public JObject RecordModelEvidence(string id, string model, bool toolsVerified, string sourceUrl)
        {
            lock (_gate)
            {
                var item = _providers.OfType<JObject>().FirstOrDefault(value => string.Equals((string)value["id"], id, StringComparison.Ordinal));
                if (item == null) throw new KeyNotFoundException("API 配置不存在");
                model = (model ?? "").Trim();
                if (model.Length == 0) throw new ArgumentException("模型不能为空", "model");
                var capabilities = item["capabilities"] as JObject;
                if (capabilities == null) { capabilities = new JObject(); item["capabilities"] = capabilities; }
                capabilities["schemaVersion"] = 2;
                capabilities["evidencePolicy"] = "live-validation";
                var models = capabilities["models"] as JObject;
                if (models == null) { models = new JObject(); capabilities["models"] = models; }
                var evidence = models[model] as JObject ?? new JObject();
                evidence["chat"] = true;
                if (toolsVerified) evidence["tools"] = true;
                else if (evidence["tools"] == null) evidence["tools"] = JValue.CreateNull();
                if (evidence["vision"] == null) evidence["vision"] = JValue.CreateNull();
                if (evidence["imageGeneration"] == null) evidence["imageGeneration"] = false;
                if (evidence["contextWindow"] == null) evidence["contextWindow"] = JValue.CreateNull();
                if (evidence["maxOutputTokens"] == null) evidence["maxOutputTokens"] = JValue.CreateNull();
                evidence["evidence"] = toolsVerified ? "live-chat-tool-call" : "live-chat-response";
                evidence["sourceUrl"] = sourceUrl ?? "";
                evidence["checkedAt"] = NowIso();
                models[model] = evidence;
                item["updatedAt"] = NowIso();
                Save();
                return Public(item);
            }
        }

        public JObject RecordAuthStyle(string id, string authStyle)
        {
            authStyle = (authStyle ?? "").Trim().ToLowerInvariant();
            if (authStyle != "bearer" && authStyle != "x-api-key") throw new ArgumentException("无效的鉴权方式", "authStyle");
            lock (_gate)
            {
                var item = _providers.OfType<JObject>().FirstOrDefault(value => string.Equals((string)value["id"], id, StringComparison.Ordinal));
                if (item == null) throw new KeyNotFoundException("API 配置不存在");
                item["authStyle"] = authStyle;
                item["updatedAt"] = NowIso();
                Save();
                return Public(item);
            }
        }

        private void Load()
        {
            lock (_gate)
            {
                _providers = JsonUtil.Read(_path, new JArray()) as JArray ?? new JArray();
                if (_providers.Count == 0)
                {
                    _providers = MigrateLegacy();
                    Save();
                }
                else if (NormalizeKnownProviders()) Save();
            }
        }

        private bool NormalizeKnownProviders()
        {
            var changed = false;
            foreach (var item in _providers.OfType<JObject>())
            {
                var text = item["text"] as JObject;
                var baseUrl = ((string)text?["baseUrl"] ?? "").Trim();
                if (text != null && baseUrl.IndexOf("api.siliconflow.cn", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    !string.Equals((string)text["protocol"], "openai", StringComparison.OrdinalIgnoreCase))
                {
                    text["protocol"] = "openai";
                    item["updatedAt"] = NowIso();
                    changed = true;
                }
            }
            return changed;
        }

        private JArray MigrateLegacy()
        {
            var output = new JArray();
            var legacy = Path.Combine(Directory.GetParent(AppPaths.Data).FullName, ".claude-gui", "providers.json");
            try
            {
                if (File.Exists(legacy))
                {
                    var token = JToken.Parse(File.ReadAllText(legacy, Encoding.UTF8));
                    var items = token as JArray ?? new JArray(token);
                    foreach (var value in items.OfType<JObject>())
                    {
                        output.Add(new JObject
                        {
                            ["id"] = (string)value["id"] ?? Guid.NewGuid().ToString(),
                            ["name"] = (string)value["name"] ?? "已迁移 API",
                            ["tokenEncrypted"] = (string)value["tokenEncrypted"] ?? "",
                            ["authStyle"] = "bearer",
                            ["text"] = new JObject
                            {
                                ["enabled"] = value["textModels"] != null && value["textModels"].Any(),
                                ["protocol"] = (string)value["format"] ?? "anthropic",
                                ["baseUrl"] = (string)value["baseUrl"] ?? "",
                                ["models"] = value["textModels"] == null ? new JArray() : value["textModels"].DeepClone()
                            },
                            ["image"] = new JObject
                            {
                                ["enabled"] = value["imageModels"] != null && value["imageModels"].Any(),
                                ["protocol"] = "openai-images",
                                ["baseUrl"] = (string)value["imageBaseUrl"] ?? "",
                                ["models"] = value["imageModels"] == null ? new JArray() : value["imageModels"].DeepClone()
                            },
                            ["createdAt"] = (string)value["createdAt"] ?? NowIso()
                        });
                    }
                }
            }
            catch (Exception error) { CrashLog.Handled("ProviderMigration", error); }

            if (output.Count == 0)
            {
                var token = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN") ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
                if (!string.IsNullOrWhiteSpace(token))
                {
                    output.Add(new JObject
                    {
                        ["id"] = "deepseek-default",
                        ["name"] = "DeepSeek（当前配置）",
                        ["tokenEncrypted"] = SecretStore.Protect(token),
                        ["authStyle"] = "bearer",
                        ["text"] = new JObject
                        {
                            ["enabled"] = true,
                            ["protocol"] = "anthropic",
                            ["baseUrl"] = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") ?? "https://api.deepseek.com/anthropic",
                            ["models"] = new JArray("deepseek-v4-pro[1m]", "deepseek-v4-flash")
                        },
                        ["image"] = new JObject { ["enabled"] = false, ["protocol"] = "openai-images", ["baseUrl"] = "", ["models"] = new JArray() },
                        ["createdAt"] = NowIso()
                    });
                }
            }
            return output;
        }

        private static JObject Capability(JObject source, string protocol)
        {
            source = source ?? new JObject();
            var models = new JArray();
            if (source["models"] is JArray array)
            {
                foreach (var value in array.Select(item => ((string)item ?? "").Trim()).Where(value => value.Length > 0).Distinct()) models.Add(value);
            }
            else
            {
                foreach (var value in ((string)source["models"] ?? "").Split(',', '，', '\n').Select(item => item.Trim()).Where(item => item.Length > 0).Distinct()) models.Add(value);
            }
            return new JObject
            {
                ["enabled"] = (bool?)source["enabled"] ?? false,
                ["protocol"] = (string)source["protocol"] ?? protocol,
                ["baseUrl"] = (((string)source["baseUrl"] ?? "").Trim().TrimEnd('/')),
                ["models"] = models
            };
        }

        private static void ValidateEndpoint(string value, string label)
        {
            Uri endpoint;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException(label + "必须是有效的 HTTP(S) 地址");
        }

        private static JObject Public(JObject item)
        {
            var copy = (JObject)item.DeepClone();
            var hasToken = !string.IsNullOrWhiteSpace((string)copy["tokenEncrypted"]);
            copy.Remove("tokenEncrypted");
            copy["hasToken"] = hasToken;
            return copy;
        }

        private void Save() { JsonUtil.WriteAtomic(_path, _providers); }
        public static string NowIso() { return DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"); }
    }

    internal static class SecretStore
    {
        public static string Protect(string value)
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        public static string Unprotect(string value)
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
    }
}

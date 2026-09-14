using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeWorkbench
{
    // Package signature v2 covers metadata AND payload. Permissions are declarations,
    // never an elevation of the selected CLI's runtime policy.
    internal static class ExtensionPackagePolicy
    {
        public static JObject Validate(JObject manifest, string root, string appVersion, Func<string, X509Certificate2> findCertificate)
        {
            var signature = manifest["signature"] as JObject;
            var development = (bool?)manifest["development"] ?? false;
            var modern = (int?)manifest["schemaVersion"] == 2;
            if (manifest["schemaVersion"] != null && !modern && (int?)manifest["schemaVersion"] != 1)
                throw new InvalidOperationException("不支持的扩展包 schemaVersion");
            var current = VersionValue(appVersion, "工作台版本", true);
            var min = VersionValue((string)manifest["minAppVersion"], "minAppVersion", modern || signature != null);
            var max = VersionValue((string)manifest["maxAppVersion"], "maxAppVersion", false);
            if (min != null && max != null && min > max) throw new InvalidOperationException("扩展兼容版本区间倒置");
            if ((min != null && current < min) || (max != null && current > max))
                throw new InvalidOperationException("当前工作台不在扩展包声明的兼容版本范围内");
            var permissions = Permissions(manifest, modern || signature != null);
            if (signature == null)
            {
                if (!development) throw new InvalidOperationException("生产扩展包必须包含受信任发布者签名");
                return new JObject { ["signatureStatus"] = "unsigned-development", ["publisherThumbprint"] = "",
                    ["permissions"] = permissions, ["permissionPolicy"] = "declaration-only-no-elevation",
                    ["warning"] = "本地未签名开发包；安装表示本机授权，不代表发布者身份可信。" };
            }
            if (!modern || (int?)signature["version"] != 2)
                throw new InvalidOperationException("旧版签名没有保护权限和版本元数据，请发布者使用 schemaVersion:2 / signature.version:2 重新签名");
            VersionValue((string)manifest["version"], "version", true);
            if ((string)signature["algorithm"] != "RSA-SHA256") throw new InvalidOperationException("签名算法仅支持 RSA-SHA256");
            var thumbprint = (string)signature["thumbprint"] ?? "";
            if (!Regex.IsMatch(thumbprint, "^[A-Fa-f0-9]{40,64}$")) throw new InvalidOperationException("发布者指纹格式无效");
            VerifyPayload(manifest, root);
            using (var certificate = findCertificate(thumbprint.ToUpperInvariant()))
            {
                if (certificate == null) throw new InvalidOperationException("发布者证书未受本机信任");
                using (var rsa = certificate.GetRSAPublicKey())
                    if (rsa == null || rsa.KeySize < 2048 || !VerifySignature(manifest, rsa))
                        throw new InvalidOperationException("扩展包签名无效，元数据或内容可能已被修改");
            }
            return new JObject { ["signatureStatus"] = "trusted", ["publisherThumbprint"] = thumbprint.ToUpperInvariant(),
                ["signatureVersion"] = 2, ["permissions"] = permissions, ["permissionPolicy"] = "declaration-only-no-elevation" };
        }

        private static Version VersionValue(string value, string field, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required) throw new InvalidOperationException("扩展包缺少 " + field);
                return null;
            }
            Version parsed;
            if (!Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9.-]+)?$") ||
                !Version.TryParse(value.Split('-')[0], out parsed)) throw new InvalidOperationException(field + " 格式无效");
            return parsed;
        }

        private static JArray Permissions(JObject manifest, bool required)
        {
            var type = (string)manifest["type"] ?? "skin";
            var allowed = type == "skin" ? new[] { "ui-theme" } :
                type == "skill" || type == "agent" ? new[] { "prompt-instructions", "bundled-scripts" } : new string[0];
            if (allowed.Length == 0) throw new InvalidOperationException("扩展包类型无效");
            var list = manifest["permissions"] as JArray;
            if (list == null)
            {
                if (required || manifest["permissions"] != null) throw new InvalidOperationException("扩展包需要 permissions 数组");
                return new JArray(); // Legacy local packages remain explicitly unsigned.
            }
            if (list.Any(item => item.Type != JTokenType.String || !allowed.Contains((string)item)) ||
                list.Values<string>().Distinct(StringComparer.Ordinal).Count() != list.Count)
                throw new InvalidOperationException("扩展权限声明无效或不受支持；插件不能自行获得进程、网络或文件权限");
            if (!list.Values<string>().Contains(allowed[0])) throw new InvalidOperationException("扩展包缺少必要权限声明：" + allowed[0]);
            return (JArray)list.DeepClone();
        }

        internal static byte[] SigningBytes(JObject manifest)
        {
            var clone = (JObject)manifest.DeepClone();
            (clone["signature"] as JObject)?.Remove("value");
            return Encoding.UTF8.GetBytes("OpenAgentWorkbench-Package-v2\n" + Canonical(clone).ToString(Formatting.None));
        }

        private static JToken Canonical(JToken value)
        {
            var obj = value as JObject;
            if (obj != null) return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new JProperty(p.Name, Canonical(p.Value))));
            var array = value as JArray;
            return array != null ? new JArray(array.Select(Canonical)) : value.DeepClone();
        }

        internal static bool VerifySignature(JObject manifest, RSA rsa)
        {
            try { return rsa.VerifyData(SigningBytes(manifest), Convert.FromBase64String((string)manifest["signature"]?["value"] ?? ""), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); }
            catch (FormatException) { return false; }
        }

        internal static void VerifyPayload(JObject manifest, string root)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var hashes = manifest["contentSha256"] as JObject;
            if (hashes == null || hashes.Count == 0 || hashes.Count > 300) throw new InvalidOperationException("扩展内容清单为空或过大");
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in hashes.Properties())
            {
                var relative = property.Name;
                if (relative.Contains("\\") || relative.Split('/').Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")) ||
                    relative.Contains(":") || !declared.Add(relative) || relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("签名清单路径无效或重复");
                var full = Path.GetFullPath(Path.Combine(root, relative));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidOperationException("签名文件缺失或越界");
                var expected = (string)property.Value ?? "";
                if (!Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$")) throw new InvalidOperationException("内容指纹格式无效");
                using (var sha = SHA256.Create()) using (var stream = File.OpenRead(full))
                    if (!string.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("扩展内容校验失败：" + relative);
                if (new[] { ".py", ".js", ".ts", ".ps1", ".sh" }.Contains(Path.GetExtension(full).ToLowerInvariant()) &&
                    !(manifest["permissions"] as JArray ?? new JArray()).Values<string>().Contains("bundled-scripts"))
                    throw new InvalidOperationException("含脚本的扩展需要声明 bundled-scripts");
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(root.Length).Replace('\\', '/');
                if (!relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) && !declared.Contains(relative))
                    throw new InvalidOperationException("扩展存在未签名文件：" + relative);
            }
        }
    }
}

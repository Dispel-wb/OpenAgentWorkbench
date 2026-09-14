using System;
using System.Net;

namespace ClaudeCodeWorkbench
{
    internal static class HttpQuery
    {
        public static string Get(HttpListenerRequest request, string key) { return Get(request.Url.Query, key); }
        internal static string Get(string query, string key)
        {
            // HttpListener.QueryString follows ContentEncoding, which can be ANSI for
            // bodyless GETs. URL percent escapes from the UI are always UTF-8.
            string result = null;
            foreach (var pair in (query ?? "").TrimStart('?').Split('&'))
            {
                var equals = pair.IndexOf('=');
                var name = equals < 0 ? pair : pair.Substring(0, equals);
                if (!string.Equals(Decode(name), key, StringComparison.Ordinal)) continue;
                if (result != null) throw new InvalidOperationException("查询参数重复：" + key);
                result = Decode(equals < 0 ? "" : pair.Substring(equals + 1));
            }
            return result;
        }
        private static string Decode(string value)
        {
            if (value.Length > 65536) throw new InvalidOperationException("查询参数过长");
            return Uri.UnescapeDataString(value.Replace("+", " "));
        }
    }
}

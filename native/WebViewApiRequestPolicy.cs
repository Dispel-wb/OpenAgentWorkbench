using System;

namespace ClaudeCodeWorkbench
{
    public static class WebViewApiRequestPolicy
    {
        public static bool IsTrusted(string baseUrl, string origin, string referer, string secFetchSite)
        {
            Uri app;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out app)) return false;
            if (SameOrigin(app, origin) || SameOrigin(app, referer)) return true;
            return string.Equals((secFetchSite ?? "").Trim(), "same-origin", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameOrigin(Uri app, string candidate)
        {
            Uri value;
            if (string.IsNullOrWhiteSpace(candidate) || !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out value)) return false;
            return string.Equals(app.Scheme, value.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(app.Host, value.Host, StringComparison.OrdinalIgnoreCase)
                && app.Port == value.Port;
        }
    }
}

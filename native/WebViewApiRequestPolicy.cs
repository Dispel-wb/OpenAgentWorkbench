using System;

namespace ClaudeCodeWorkbench
{
    public static class WebViewApiRequestPolicy
    {
        public static bool IsTrusted(string baseUrl, string origin, string referer, string secFetchSite)
        {
            Uri app;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out app)) return false;
            var hasOrigin = !string.IsNullOrWhiteSpace(origin);
            var hasReferer = !string.IsNullOrWhiteSpace(referer);
            // Provenance headers must agree. Never let a same-origin signal or a
            // second trusted header override an explicitly external value.
            if (hasOrigin && !SameOrigin(app, origin)) return false;
            if (hasReferer && !SameOrigin(app, referer)) return false;
            if (hasOrigin || hasReferer) return true;
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

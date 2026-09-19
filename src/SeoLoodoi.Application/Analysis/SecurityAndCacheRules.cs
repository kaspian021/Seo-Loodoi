using System.Text.Json;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed class HstsMissingRule : ISeoRule
{
    public string Code => "HSTS_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var isHttps = Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
        if (!isHttps || c.StatusCode != 200)
            return new(Code, false, IssueSeverity.Medium, IssueCategory.Security, null);

        var hasHsts = HasHeader(c.HeadersJson, "Strict-Transport-Security");
        return new(Code, !hasHsts, IssueSeverity.Medium, IssueCategory.Security,
            !hasHsts ? new("hstsHeader", "Missing", "Strict-Transport-Security with max-age >= 10886400") : null);
    }

    private static bool HasHeader(string? headersJson, string headerName)
    {
        if (string.IsNullOrWhiteSpace(headersJson) || headersJson == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(headersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, headerName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Ignore parse errors
        }
        return false;
    }
}

public sealed class SecurityHeadersMissingRule : ISeoRule
{
    public string Code => "SECURITY_HEADERS_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (c.StatusCode != 200 || string.IsNullOrWhiteSpace(c.HeadersJson) || c.HeadersJson == "{}")
            return new(Code, false, IssueSeverity.Low, IssueCategory.Security, null);

        var missing = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(c.HeadersJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var hasContentTypeOptions = false;
                var hasFrameOptions = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "X-Content-Type-Options", StringComparison.OrdinalIgnoreCase))
                        hasContentTypeOptions = true;
                    if (string.Equals(prop.Name, "X-Frame-Options", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(prop.Name, "Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
                        hasFrameOptions = true;
                }
                if (!hasContentTypeOptions) missing.Add("X-Content-Type-Options");
                if (!hasFrameOptions) missing.Add("X-Frame-Options / CSP");
            }
        }
        catch
        {
            return new(Code, false, IssueSeverity.Low, IssueCategory.Security, null);
        }

        var triggered = missing.Count > 0;
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Security,
            triggered ? new("missingSecurityHeaders", string.Join(", ", missing), "Defensive HTTP headers (X-Content-Type-Options, X-Frame-Options)") : null);
    }
}

public sealed class CacheControlMissingRule : ISeoRule
{
    public string Code => "CACHE_CONTROL_MISSING";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (c.StatusCode != 200)
            return new(Code, false, IssueSeverity.Low, IssueCategory.Technical, null);

        var hasCache = false;
        if (!string.IsNullOrWhiteSpace(c.HeadersJson) && c.HeadersJson != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(c.HeadersJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "Cache-Control", StringComparison.OrdinalIgnoreCase))
                        {
                            hasCache = true;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }

        return new(Code, !hasCache, IssueSeverity.Low, IssueCategory.Technical,
            !hasCache ? new("cacheControl", "Missing", "Explicit Cache-Control policy header") : null);
    }
}

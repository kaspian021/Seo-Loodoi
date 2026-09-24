using System.Text.Json;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

/// <summary>
/// Deterministic meta robots / X-Robots-Tag directive parsing shared by the
/// page-level directive rules and the site-level robots analysis.
/// </summary>
public static class RobotsDirectives
{
    /// <summary>Splits a robots directive list ("noindex, nofollow") into lowercase tokens.</summary>
    public static IReadOnlyList<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToArray();
    }

    public static bool Has(string? raw, string directive) => Parse(raw).Contains(directive);
}

/// <summary>meta robots nofollow: search engines should not follow links on this page.</summary>
public sealed class MetaRobotsNoFollowRule : ISeoRule
{
    public string Code => "META_ROBOTS_NOFOLLOW";
    public SeoRuleResult Evaluate(PageAnalysisContext c) =>
        new(Code, RobotsDirectives.Has(c.RobotsMeta, "nofollow"), IssueSeverity.Medium, IssueCategory.Indexability,
            new("robotsMeta", c.RobotsMeta, "no nofollow directive on a linked page"));
}

/// <summary>meta robots noarchive: cached copies are disallowed.</summary>
public sealed class MetaRobotsNoArchiveRule : ISeoRule
{
    public string Code => "META_ROBOTS_NOARCHIVE";
    public SeoRuleResult Evaluate(PageAnalysisContext c) =>
        new(Code, RobotsDirectives.Has(c.RobotsMeta, "noarchive"), IssueSeverity.Notice, IssueCategory.Indexability,
            new("robotsMeta", c.RobotsMeta, "no noarchive directive unless intended"));
}

/// <summary>meta robots nosnippet: snippets and video previews are suppressed in results.</summary>
public sealed class MetaRobotsNoSnippetRule : ISeoRule
{
    public string Code => "META_ROBOTS_NOSNIPPET";
    public SeoRuleResult Evaluate(PageAnalysisContext c) =>
        new(Code, RobotsDirectives.Has(c.RobotsMeta, "nosnippet"), IssueSeverity.Medium, IssueCategory.Indexability,
            new("robotsMeta", c.RobotsMeta, "no nosnippet directive unless intended"));
}

/// <summary>meta robots noimageindex: images on this page are excluded from image search.</summary>
public sealed class MetaRobotsNoImageIndexRule : ISeoRule
{
    public string Code => "META_ROBOTS_NOIMAGEINDEX";
    public SeoRuleResult Evaluate(PageAnalysisContext c) =>
        new(Code, RobotsDirectives.Has(c.RobotsMeta, "noimageindex"), IssueSeverity.Low, IssueCategory.Indexability,
            new("robotsMeta", c.RobotsMeta, "no noimageindex directive unless intended"));
}

/// <summary>X-Robots-Tag nofollow: the HTTP header disables link following.</summary>
public sealed class XRobotsNoFollowRule : ISeoRule
{
    public string Code => "X_ROBOTS_NOFOLLOW";
    public SeoRuleResult Evaluate(PageAnalysisContext c) =>
        new(Code, RobotsDirectives.Has(c.XRobotsTag, "nofollow"), IssueSeverity.Medium, IssueCategory.Indexability,
            new("xRobotsTag", c.XRobotsTag, "no nofollow directive on an indexable page"));
}

/// <summary>
/// Indexability contradiction: the meta robots directive and the X-Robots-Tag header
/// disagree about noindex, or one source lists both "index" and "noindex".
/// </summary>
public sealed class IndexabilityContradictionRule : ISeoRule
{
    public string Code => "INDEXABILITY_CONTRADICTION";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var metaNoIndex = RobotsDirectives.Has(c.RobotsMeta, "noindex");
        var headerNoIndex = RobotsDirectives.Has(c.XRobotsTag, "noindex");
        var metaConflict = RobotsDirectives.Has(c.RobotsMeta, "index") && metaNoIndex;
        var headerConflict = RobotsDirectives.Has(c.XRobotsTag, "index") && headerNoIndex;
        // Source disagreement counts only when both sources speak at all.
        var sourceDisagreement = c.RobotsMeta is not null && c.XRobotsTag is not null && metaNoIndex != headerNoIndex;
        var triggered = metaConflict || headerConflict || sourceDisagreement;
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Indexability,
            triggered ? new("conflictingDirectives",
                $"robotsMeta={c.RobotsMeta ?? "<none>"}; xRobotsTag={c.XRobotsTag ?? "<none>"}",
                "One consistent indexability directive across meta robots and X-Robots-Tag") : null);
    }
}

/// <summary>Canonical URL points at a different host (cross-host canonicalisation).</summary>
public sealed class CanonicalExternalRule : ISeoRule
{
    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    public string Code => "CANONICAL_EXTERNAL";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (string.IsNullOrWhiteSpace(c.Canonical) || !Uri.TryCreate(c.Canonical, UriKind.Absolute, out var canonical)
            || !Uri.TryCreate(c.Url, UriKind.Absolute, out var page)) return new(Code, false, IssueSeverity.Medium, IssueCategory.Indexability, null);
        // "www." is the same site: a www/non-www canonical is host-standardisation,
        // not cross-host canonicalisation (that variance is a URL-variant check).
        var triggered = !string.Equals(StripWww(canonical.IdnHost), StripWww(page.IdnHost), StringComparison.OrdinalIgnoreCase);
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Indexability,
            triggered ? new("canonicalHost", canonical.IdnHost, $"Same host as the page ({page.IdnHost})") : null);
    }
}

/// <summary>
/// Soft 404 heuristic: the URL answers 200 but behaves like a missing page (an
/// explicit "not found" marker in the title, or essentially no text). Diagnostic
/// heuristic, not a ranking factor.
/// </summary>
public sealed class Soft404Rule : ISeoRule
{
    private static readonly string[] Markers = ["404", "not found", "page not found", "پیدا نشد", "یافت نشد", "صفحه یافت نشد", "پیدا نشد"];
    public string Code => "SOFT_404";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (c.StatusCode != 200) return new(Code, false, IssueSeverity.Medium, IssueCategory.Technical, null);
        var title = c.Title ?? string.Empty;
        var marker = Markers.FirstOrDefault(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));
        var emptyBody = c.WordCount <= 5;
        // The title marker alone can appear on real pages (e.g. "how to fix 404
        // errors"); it only indicates a soft 404 together with thin content.
        var triggered = emptyBody || (marker is not null && c.WordCount < 100);
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Technical,
            triggered ? new("soft404Signal", marker ?? $"wordCount={c.WordCount}", "A 200 page with unique, real content") : null);
    }
}

/// <summary>Redirect chain contains a repeated URL (a redirect loop survived fetch-time detection).</summary>
public sealed class RedirectLoopRule : ISeoRule
{
    public string Code => "REDIRECT_LOOP";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var chain = ParseChain(c.RedirectChainJson);
        var triggered = chain.Count > 1 && chain.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        return new(Code, triggered, IssueSeverity.Medium, IssueCategory.Technical,
            triggered ? new("redirectChain", string.Join(" -> ", chain), "An acyclic redirect chain") : null);
    }

    private static IReadOnlyList<string> ParseChain(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
                : [];
        }
        catch (JsonException) { return []; }
    }
}

/// <summary>The crawled page is not a type we can extract SEO facts from (HTML/XML/PDF/text).</summary>
public sealed class ContentTypeUnsupportedRule : ISeoRule
{
    private static readonly string[] SupportedPrefixes = ["text/", "application/xhtml", "application/xml", "application/pdf", "application/atom", "application/rss", "application/json"];
    public string Code => "CONTENT_TYPE_UNSUPPORTED";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var type = c.ContentType?.Trim().ToLowerInvariant();
        var unsupported = !string.IsNullOrWhiteSpace(type) && !SupportedPrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal));
        return new(Code, unsupported, IssueSeverity.Low, IssueCategory.Technical,
            unsupported ? new("contentType", type, "text/html (or another extractable text type)") : null);
    }
}

/// <summary>URL length heuristic — long URLs are harder to crawl, share and parse.</summary>
public sealed class UrlTooLongRule(int maxLength = 120) : ISeoRule
{
    public string Code => "URL_TOO_LONG";
    public SeoRuleResult Evaluate(PageAnalysisContext c) =>
        new(Code, c.Url.Length > maxLength, IssueSeverity.Low, IssueCategory.Technical,
            new("urlLength", c.Url.Length.ToString(), $"<= {maxLength} characters (heuristic)"));
}

/// <summary>Excessive path depth — unrelated to link click depth (DEEP_CLICK_DEPTH).</summary>
public sealed class UrlTooDeepRule(int maxSegments = 6) : ISeoRule
{
    public string Code => "URL_TOO_DEEP";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var segments = Uri.TryCreate(c.Url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        return new(Code, segments > maxSegments, IssueSeverity.Low, IssueCategory.Technical,
            new("pathSegments", segments.ToString(), $"<= {maxSegments} path segments (heuristic)"));
    }
}

/// <summary>Uppercase characters in the path create duplicate-URL risks on case-insensitive hosts.</summary>
public sealed class UrlUpperCaseRule : ISeoRule
{
    public string Code => "URL_UPPERCASE";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var path = Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : string.Empty;
        var triggered = path.Any(char.IsUpper);
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Technical,
            triggered ? new("urlPath", path, "lowercase path (case-insensitive duplicate risk)") : null);
    }
}

/// <summary>Suspicious percent-encoding: double encoding ("%25") or encoded unreserved characters.</summary>
public sealed class UrlEncodingAnomalyRule : ISeoRule
{
    public string Code => "URL_ENCODING_ANOMALY";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        // Scan the raw stored URL: Uri canonicalisation would unescape %41-style
        // encodings to literal characters and hide exactly the anomaly we check for.
        var raw = c.Url;
        var doubleEncoded = raw.Contains("%25", StringComparison.OrdinalIgnoreCase);
        // %41-%5A / %61-%7A are encoded ASCII letters — an unreserved-character encoding anomaly.
        var encodedLetter = System.Text.RegularExpressions.Regex.IsMatch(raw, "%(4[1-9A-Fa-f]|5[0-9A-Fa-f]|6[1-9A-Fa-f]|7[0-9A-Fa-f])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var triggered = doubleEncoded || encodedLetter;
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Technical,
            triggered ? new("pathAndQuery", raw, "No double encoding or encoded unreserved characters") : null);
    }
}

/// <summary>A fragment identifier in a crawled URL usually signals a variant or a JS route.</summary>
public sealed class UrlFragmentRule : ISeoRule
{
    public string Code => "URL_FRAGMENT";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        var triggered = Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Fragment);
        return new(Code, triggered, IssueSeverity.Low, IssueCategory.Technical,
            triggered ? new("fragment", uri!.Fragment, "No fragment on an indexable URL") : null);
    }
}

/// <summary>Known analytics/advertising tracking parameters create crawl-bait URL variants.</summary>
public sealed class UrlTrackingParameterRule : ISeoRule
{
    private static readonly string[] TrackingKeys = ["utm_", "gclid", "fbclid", "msclkid", "mc_cid", "mc_eid", "_ga", "igshid"];
    public string Code => "URL_TRACKING_PARAMETER";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return new(Code, false, IssueSeverity.Low, IssueCategory.Technical, null);
        var keys = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)[0].ToLowerInvariant()).ToArray();
        var tracking = keys.Where(k => TrackingKeys.Any(t => k.StartsWith(t, StringComparison.Ordinal))).Distinct().ToArray();
        return new(Code, tracking.Length > 0, IssueSeverity.Low, IssueCategory.Technical,
            tracking.Length > 0 ? new("trackingParameters", string.Join(",", tracking), "No tracking parameters in indexable URLs") : null);
    }
}

/// <summary>The same query parameter key appears multiple times in one URL.</summary>
public sealed class UrlParameterDuplicateRule : ISeoRule
{
    public string Code => "URL_PARAMETER_DUPLICATE";
    public SeoRuleResult Evaluate(PageAnalysisContext c)
    {
        if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return new(Code, false, IssueSeverity.Low, IssueCategory.Technical, null);
        var keys = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)[0].ToLowerInvariant()).ToArray();
        var duplicated = keys.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        return new(Code, duplicated.Length > 0, IssueSeverity.Low, IssueCategory.Technical,
            duplicated.Length > 0 ? new("duplicatedParameters", string.Join(",", duplicated), "Each query parameter appears once") : null);
    }
}

using System.Text.Json;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Aeo;

/// <summary>
/// Deterministic AEO/GEO engine.
/// <para>
/// Every score is derived from stored crawl evidence - robots.txt text, headings,
/// JSON-LD and page text. Nothing is estimated, and when evidence is missing the
/// score is <c>null</c> rather than zero: an unmeasured score must stay visibly
/// unmeasured, which is the same discipline the backlink and SERP engines follow.
/// </para>
/// </summary>
public sealed class AeoAnalyzer(IRobotsParser robotsParser) : IAeoAnalyzer
{
    private const string Unspecified = "Unspecified";
    private const string Allowed = "Allowed";
    private const string Blocked = "Blocked";

    private static readonly string[] EntitySchemaTypes =
        ["Organization", "Article", "Product", "Person", "LocalBusiness", "NewsArticle", "BlogPosting"];

    private static readonly string[] QuestionStarters =
    [
        "what", "how", "why", "when", "where", "who", "which", "is", "are", "can", "do", "does", "should",
        "چیست", "چی", "چه", "چگونه", "چطور", "چرا", "کی", "کجا", "کدام", "آیا", "چند"
    ];

    public AiVisibilityReportDto Analyze(
        Guid projectId,
        Guid crawlId,
        string? robotsTxt,
        Uri siteOrigin,
        IReadOnlyList<AiCrawlerProfileDto> profiles,
        IReadOnlyList<AeoPageInput> pages)
    {
        var findings = new List<string>();
        var enabled = profiles.Where(p => p.IsEnabled).ToArray();

        var access = AssessCrawlers(robotsTxt, siteOrigin, enabled, findings, out var robotsAvailable);
        var crawlability = robotsAvailable ? WeightedCrawlability(access) : null;

        var signals = AssessPages(pages);
        var answerReadiness = signals.PagesAnalyzed == 0 ? null : (decimal?)AnswerReadiness(signals);
        var citationReadiness = signals.PagesAnalyzed == 0 ? null : (decimal?)CitationReadiness(signals);
        var contentAccessibility = signals.PagesAnalyzed == 0 ? null : (decimal?)ContentAccessibility(signals);

        decimal? visibility = null;
        if (crawlability is { } c && answerReadiness is { } a && citationReadiness is { } t && contentAccessibility is { } x)
            visibility = decimal.Round(c * 0.40m + a * 0.25m + t * 0.25m + x * 0.10m, 1);

        if (signals.PagesAnalyzed == 0)
            findings.Add("هیچ صفحه‌ای با محتوای قابل تحلیل یافت نشد؛ امتیازهای پاسخ‌گویی و استناد نامشخص هستند.");
        if (!robotsAvailable)
            findings.Add("فایل robots.txt در این خزش در دسترس نبود؛ امتیاز دسترسی‌پذیری خزنده‌های هوش مصنوع نامشخص است.");

        return new AiVisibilityReportDto(
            projectId, crawlId, crawlability, answerReadiness, citationReadiness, visibility,
            access, signals, findings, robotsAvailable);
    }

    // ------------------------------------------------------------- crawlers

    private IReadOnlyList<AiCrawlerAccessDto> AssessCrawlers(
        string? robotsTxt,
        Uri siteOrigin,
        IReadOnlyList<AiCrawlerProfileDto> profiles,
        List<string> findings,
        out bool robotsAvailable)
    {
        robotsAvailable = !string.IsNullOrWhiteSpace(robotsTxt);
        RobotsDocument? document = null;
        if (robotsAvailable)
        {
            try { document = robotsParser.Parse(robotsTxt!, siteOrigin); }
            catch (Exception) { document = null; robotsAvailable = false; }
        }

        var results = new List<AiCrawlerAccessDto>();
        foreach (var profile in profiles)
        {
            var state = Classify(document, profile.UserAgentToken, siteOrigin);
            results.Add(new AiCrawlerAccessDto(profile.Key, profile.DisplayName, profile.UserAgentToken, profile.Purpose, state, profile.Weight));

            if (state == Blocked)
                findings.Add($"خزنده «{profile.DisplayName}» ({profile.UserAgentToken}) در robots.txt مسدود شده است.");
        }

        if (robotsAvailable)
        {
            var blocked = results.Count(x => x.Access == Blocked);
            var unspecified = results.Count(x => x.Access == Unspecified);
            if (blocked > 0) findings.Add($"{blocked} خزنده هوش مصنوعی مسدود شده‌اند.");
            if (unspecified > 0) findings.Add($"{unspecified} خزنده در robots.txt نام برده نشده‌اند (رفتار پیش‌فرض اجازه است، اما صریح نیست).");
        }

        return results;
    }

    /// <summary>
    /// A crawler is Blocked when a group that applies to it disallows the root,
    /// Allowed only when a group naming it explicitly permits the root, and
    /// Unspecified when no group mentions it (it then inherits the wildcard or
    /// the robots-free default, which is not an explicit decision by the site).
    /// </summary>
    private static string Classify(RobotsDocument? document, string userAgent, Uri siteOrigin)
    {
        if (document is null) return Unspecified;

        var token = userAgent.Trim().ToLowerInvariant();
        var specific = document.Groups
            .Where(g => g.UserAgents.Any(a => !string.IsNullOrWhiteSpace(a) && a.Trim() != "*" && AgentMatches(a, token)))
            .ToArray();
        var wildcard = document.Groups
            .Where(g => g.UserAgents.Any(a => a.Trim() == "*"))
            .ToArray();

        if (specific.Length > 0)
            return specific.Any(g => AllowsRoot(g, siteOrigin)) ? Allowed : Blocked;

        if (wildcard.Length > 0)
            return wildcard.Any(g => AllowsRoot(g, siteOrigin)) ? Unspecified : Blocked;

        return Unspecified;
    }

    private static bool AgentMatches(string groupAgent, string crawlerToken)
    {
        var a = groupAgent.Trim().ToLowerInvariant();
        return a == crawlerToken || crawlerToken.Contains(a, StringComparison.Ordinal) || a.Contains(crawlerToken, StringComparison.Ordinal);
    }

    private static bool AllowsRoot(RobotsGroup group, Uri siteOrigin)
    {
        var root = new Uri(siteOrigin, "/");
        return group.Rules.Count == 0 || group.Rules.All(r => r.Allow) || documentAllows(group, root);
    }

    private static bool documentAllows(RobotsGroup group, Uri root)
    {
        // Reuse the same precedence the crawler itself uses rather than
        // re-implementing robots matching: longest, most specific pattern wins.
        var matches = group.Rules
            .Select(r => (Rule: r, Score: PatternSpecificity(r.Pattern, root)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Rule.Allow)
            .ToArray();
        return matches.Length == 0 || matches[0].Rule.Allow;
    }

    private static int PatternSpecificity(string pattern, Uri root)
    {
        var path = root.PathAndQuery;
        var p = pattern ?? string.Empty;
        if (p.Length == 0) return 0;
        var endAnchored = p.EndsWith('$');
        if (endAnchored) p = p[..^1];
        if (p.Length == 0) return path.Length == 1 ? 2 : -1;
        var normalized = p.Replace("*", string.Empty);
        if (endAnchored)
            return path.Equals(p, StringComparison.Ordinal) ? int.MaxValue : -1;
        return path.StartsWith(normalized, StringComparison.Ordinal) ? normalized.Length : -1;
    }

    private static decimal? WeightedCrawlability(IReadOnlyList<AiCrawlerAccessDto> access)
    {
        var scored = access.Where(x => x.Weight > 0).ToArray();
        if (scored.Length == 0) return null;

        var totalWeight = scored.Sum(x => x.Weight);
        if (totalWeight <= 0) return null;

        var sum = scored.Sum(x => x.Weight * (x.Access switch
        {
            Allowed => 100m,
            Unspecified => 70m,
            _ => 0m
        }));
        return decimal.Round(sum / totalWeight, 1);
    }

    // ---------------------------------------------------------------- pages

    private static AeoSignalsDto AssessPages(IReadOnlyList<AeoPageInput> pages)
    {
        var withText = pages.Where(p => !string.IsNullOrWhiteSpace(p.TextContent)).ToArray();

        return new AeoSignalsDto(
            PagesAnalyzed: withText.Length,
            PagesWithQuestionHeadings: withText.Count(p => HasQuestionHeading(p.HeadingsJson)),
            PagesWithFaqSchema: withText.Count(p => HasSchemaType(p.SchemaJson, "FAQPage", "QAPage")),
            PagesWithAnySchema: withText.Count(p => HasAnySchema(p.SchemaJson)),
            PagesWithEntitySchema: withText.Count(p => HasSchemaType(p.SchemaJson, EntitySchemaTypes)),
            PagesWithAuthorOrDate: withText.Count(p => HasAuthorOrDate(p.SchemaJson)),
            PagesWithCanonical: withText.Count(p => !string.IsNullOrWhiteSpace(p.Canonical)),
            PagesWithConciseAnswer: withText.Count(p => p.WordCount is >= 150 and <= 1200),
            PagesWithHeadingStructure: withText.Count(p => CountHeadings(p.HeadingsJson) >= 3));
    }

    private static decimal AnswerReadiness(AeoSignalsDto s)
    {
        var n = Math.Max(1, s.PagesAnalyzed);
        var questionRate = s.PagesWithQuestionHeadings / (decimal)n;
        var faqRate = s.PagesWithFaqSchema / (decimal)n;
        var conciseRate = s.PagesWithConciseAnswer / (decimal)n;
        var structureRate = s.PagesWithHeadingStructure / (decimal)n;
        return decimal.Round((questionRate * 30m) + (faqRate * 20m) + (conciseRate * 20m) + (structureRate * 30m), 1);
    }

    private static decimal CitationReadiness(AeoSignalsDto s)
    {
        var n = Math.Max(1, s.PagesAnalyzed);
        var anySchema = s.PagesWithAnySchema / (decimal)n;
        var entity = s.PagesWithEntitySchema / (decimal)n;
        var canonical = s.PagesWithCanonical / (decimal)n;
        var authorDate = s.PagesWithAuthorOrDate / (decimal)n;
        return decimal.Round((anySchema * 30m) + (entity * 30m) + (canonical * 20m) + (authorDate * 20m), 1);
    }

    private static decimal ContentAccessibility(AeoSignalsDto s)
    {
        // Text present in the raw HTML is machine-readable without executing
        // JavaScript. That is the strongest accessibility signal available from a
        // non-rendering crawl.
        var n = Math.Max(1, s.PagesAnalyzed);
        return decimal.Round(s.PagesWithConciseAnswer / (decimal)n * 100m, 1);
    }

    // -------------------------------------------------------------- parsing

    private static IReadOnlyList<(int Level, string Text)> ParseHeadings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var list = new List<(int, string)>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var text = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var level = item.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var lv) ? lv : 0;
                if (!string.IsNullOrWhiteSpace(text)) list.Add((level, text!));
            }
            return list;
        }
        catch (JsonException) { return []; }
    }

    private static int CountHeadings(string? json) => ParseHeadings(json).Count;

    private static bool HasQuestionHeading(string? headingsJson)
    {
        foreach (var (_, text) in ParseHeadings(headingsJson))
        {
            var t = text.Trim();
            if (t.EndsWith('?') || t.EndsWith('؟')) return true;
            var first = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (QuestionStarters.Any(q => first.Equals(q, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static IReadOnlyList<string> SchemaTypes(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Trim() is "[]" or "{}" or "null") return [];
        var types = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            Collect(doc.RootElement);
        }
        catch (JsonException) { return []; }
        return types;

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Collect(item);
                    break;
                case JsonValueKind.Object:
                    if (element.TryGetProperty("@type", out var type))
                    {
                        if (type.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var t in element.GetProperty("@type").EnumerateArray())
                                if (t.ValueKind == JsonValueKind.String) types.Add(t.GetString()!);
                        }
                        else if (type.ValueKind == JsonValueKind.String)
                        {
                            types.Add(type.GetString()!);
                        }
                    }
                    if (element.TryGetProperty("@graph", out var graph)) Collect(graph);
                    break;
            }
        }
    }

    private static bool HasAnySchema(string? schemaJson) => SchemaTypes(schemaJson).Count > 0;

    private static bool HasSchemaType(string? schemaJson, params string[] wanted) =>
        SchemaTypes(schemaJson).Any(t => wanted.Any(w => t.Equals(w, StringComparison.OrdinalIgnoreCase)));

    private static bool HasAuthorOrDate(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Trim() is "[]" or "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            return Contains(doc.RootElement);
        }
        catch (JsonException) { return false; }

        static bool Contains(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray().Any(Contains),
            JsonValueKind.Object =>
                (element.TryGetProperty("author", out _) || element.TryGetProperty("datePublished", out _) || element.TryGetProperty("dateModified", out _))
                || (element.TryGetProperty("@graph", out var graph) && Contains(graph)),
            _ => false
        };
    }
}

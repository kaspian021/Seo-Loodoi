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
        // Use the crawler's real longest-match/allow-tie semantics. A second
        // approximate matcher here can fabricate a blocked-access advisory.
        if (!document.IsAllowed(userAgent, new Uri(siteOrigin, "/"))) return Blocked;
        var named = document.Groups.Any(g => g.UserAgents.Any(agent =>
            !string.IsNullOrWhiteSpace(agent) && agent.Trim() != "*"
            && userAgent.Contains(agent.Trim(), StringComparison.OrdinalIgnoreCase)));
        return named ? Allowed : Unspecified;
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
            PagesWithHeadingStructure: withText.Count(p => CountHeadings(p.HeadingsJson) >= 3),
            PagesWithEvaluableAnswerStructure: withText.Count(p => IsObjectArray(p.HeadingsJson, headings: true) && ReadSchemas(p.SchemaJson).Valid));
    }

    private static bool IsObjectArray(string? json, bool headings = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object
                    && (!headings || (TryHeadingProperty(x, "text", "Text", out var text) && text.ValueKind == JsonValueKind.String
                        && TryHeadingProperty(x, "level", "Level", out var level) && level.ValueKind == JsonValueKind.Number)));
        }
        catch (JsonException) { return false; }
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
                var text = TryHeadingProperty(item, "text", "Text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var level = TryHeadingProperty(item, "level", "Level", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var lv) ? lv : 0;
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

    private static bool TryHeadingProperty(JsonElement item, string camel, string pascal, out JsonElement value) =>
        item.TryGetProperty(camel, out value) || item.TryGetProperty(pascal, out value);

    // The crawler stores JSON-LD as an array of script strings and headings as
    // PascalCase records. Also support object-array fixtures/older snapshots.
    private static (IReadOnlyList<JsonElement> Objects, bool Valid) ReadSchemas(string? json)
    {
        var objects = new List<JsonElement>();
        var valid = true;
        if (string.IsNullOrWhiteSpace(json)) return (objects, false);
        Parse(json, 0);
        return (objects, valid);

        void Parse(string text, int depth)
        {
            if (depth > 16) { valid = false; return; }
            try
            {
                using var doc = JsonDocument.Parse(text);
                Collect(doc.RootElement, depth);
            }
            catch (JsonException) { valid = false; }
        }
        void Collect(JsonElement element, int depth)
        {
            if (depth > 16) { valid = false; return; }
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    Parse(element.GetString()!, depth + 1);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Collect(item, depth + 1);
                    break;
                case JsonValueKind.Object:
                    objects.Add(element.Clone());
                    if (element.TryGetProperty("@graph", out var graph)) Collect(graph, depth + 1);
                    break;
                default:
                    valid = false;
                    break;
            }
        }
    }

    private static IReadOnlyList<string> SchemaTypes(string? json)
    {
        var types = new List<string>();
        foreach (var element in ReadSchemas(json).Objects)
        {
            if (!element.TryGetProperty("@type", out var type)) continue;
            if (type.ValueKind == JsonValueKind.String) types.Add(type.GetString()!);
            else if (type.ValueKind == JsonValueKind.Array)
                foreach (var item in type.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) types.Add(item.GetString()!);
        }
        return types;
    }
    private static bool HasAnySchema(string? json) => SchemaTypes(json).Count > 0;
    private static bool HasSchemaType(string? json, params string[] wanted) =>
        SchemaTypes(json).Any(t => wanted.Any(w => t.Equals(w, StringComparison.OrdinalIgnoreCase)));
    private static bool HasAuthorOrDate(string? json) => ReadSchemas(json).Objects.Any(element =>
        element.TryGetProperty("author", out _) || element.TryGetProperty("datePublished", out _) || element.TryGetProperty("dateModified", out _));
}

using System.Text.Json;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

/// <summary>
/// Where a piece of evidence was observed. Machine-readable; never prose.
/// (P1 Phase 2 evidence-first contract.)
/// </summary>
public enum EvidenceSource { RawHtml, RenderedDom, Http, Headers, Robots, Sitemap, Links, StructuredData, SearchConsole, PerformanceProvider, ExternalProvider }

/// <summary>Kind of resource a rule evaluates: a rendered page, a bare URL, an asset, a site-wide fact or a cluster of pages.</summary>
public enum RuleResourceType { Page, Url, Asset, Site, Cluster }

/// <summary>
/// Explainable sub-score a rule contributes to (P1 Phase 12). Fixed mapping to the
/// nine published scores; never derived from display text.
/// </summary>
public enum ScoreDimension
{
    /// <summary>HTTP behaviour, URL hygiene, security/caching headers.</summary>
    Technical,
    /// <summary>Indexability and canonicalisation.</summary>
    Indexability,
    /// <summary>On-page metadata, headings and content diagnostics.</summary>
    ContentTechnical,
    /// <summary>Internal link architecture and site graph.</summary>
    Architecture,
    StructuredData,
    International,
    Performance,
    /// <summary>Raw-vs-rendered JavaScript SEO differences.</summary>
    JavaScriptSeo
}

/// <summary>
/// Explicit metadata every rule carries (P1 Phase 1). Identity is always a machine
/// key: <see cref="Code"/> is the persisted stable id (SeoIssue.RuleCode) and
/// <see cref="RuleId"/> the stable dotted documentation key. Display text is never
/// an identifier.
/// </summary>
public sealed record SeoRuleMetadata(
    string RuleId,
    string Code,
    IssueCategory Category,
    IssueSeverity BaseSeverity,
    RuleResourceType ResourceType,
    ScoreDimension Dimension,
    IReadOnlyList<EvidenceSource> EvidenceSources,
    string RecommendationKey,
    string DocKey,
    string TitleKey,
    string DescriptionKey,
    string Version,
    decimal Confidence,
    decimal ScoringWeight,
    bool RequiresHtmlSnapshot,
    bool IndexabilityImpact,
    string? Prerequisites = null)
{
    public const string CurrentVersion = "1.0.0";

    public static SeoRuleMetadata Create(
        string ruleId,
        string code,
        IssueCategory category,
        IssueSeverity baseSeverity,
        RuleResourceType resourceType,
        ScoreDimension dimension,
        EvidenceSource[] evidenceSources,
        string recommendationKey,
        string docKey,
        string version = CurrentVersion,
        decimal confidence = 1m,
        decimal scoringWeight = 1m,
        bool requiresHtmlSnapshot = true,
        bool indexabilityImpact = false,
        string? prerequisites = null) =>
        new(ruleId, code, category, baseSeverity, resourceType, dimension, evidenceSources,
            recommendationKey, docKey, TitleKey: code, DescriptionKey: code,
            version, confidence, scoringWeight, requiresHtmlSnapshot, indexabilityImpact, prerequisites);
}

/// <summary>Rules that declare their metadata inline (new rules should implement this).</summary>
public interface ISeoRuleWithMetadata : ISeoRule
{
    SeoRuleMetadata Metadata { get; }
}

/// <summary>
/// One machine-readable observed fact. The underlying value is always retained —
/// prose alone ("title is bad") is never stored as the only evidence.
/// </summary>
public sealed record EvidenceFact(string Field, string? Value, EvidenceSource Source, string? Expected = null, string? Extra = null)
{
    public int? Length => Value?.Length;
}

/// <summary>
/// Evidence envelope persisted to SeoIssue.EvidenceJson (P1 Phase 2). Keeps the
/// legacy <c>Url</c>/<c>Evidence</c> keys verbatim for existing consumers and adds
/// the machine-readable contract: facts[] with sources, why, expected, rule identity,
/// confidence, detectedAt and ruleVersion.
/// </summary>
public static class SeoEvidence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Stable kebab-case name of an evidence source, as stored in evidence JSON.</summary>
    public static string SourceName(EvidenceSource source) => source switch
    {
        EvidenceSource.RawHtml => "raw-html",
        EvidenceSource.RenderedDom => "rendered-dom",
        EvidenceSource.Http => "http",
        EvidenceSource.Headers => "headers",
        EvidenceSource.Robots => "robots",
        EvidenceSource.Sitemap => "sitemap",
        EvidenceSource.Links => "links",
        EvidenceSource.StructuredData => "structured-data",
        EvidenceSource.SearchConsole => "search-console",
        EvidenceSource.PerformanceProvider => "performance-provider",
        EvidenceSource.ExternalProvider => "external-provider",
        _ => source.ToString().ToLowerInvariant()
    };

    public static string Build(
        string url,
        SeoRuleMetadata metadata,
        RuleEvidence? evidence,
        DateTimeOffset detectedAt,
        object? extra = null,
        string? why = null,
        params EvidenceFact[] facts)
    {
        var primarySource = metadata.EvidenceSources.Count > 0 ? metadata.EvidenceSources[0] : EvidenceSource.RawHtml;
        var factList = new List<Dictionary<string, object?>>(facts.Length + 1);
        if (evidence is not null)
            factList.Add(Fact(evidence.Field, evidence.Actual, primarySource, evidence.Expected, null));
        foreach (var fact in facts)
            factList.Add(Fact(fact.Field, fact.Value, fact.Source, fact.Expected, fact.Extra));

        // Legacy Evidence sub-object keeps its PascalCase property names even though
        // the envelope uses camelCase (dictionary keys ignore PropertyNamingPolicy).
        Dictionary<string, object?>? legacyEvidence = evidence is null
            ? null
            : new() { ["Field"] = evidence.Field, ["Actual"] = evidence.Actual, ["Expected"] = evidence.Expected };

        var payload = new Dictionary<string, object?>
        {
            // Legacy keys, kept byte-compatible in name and shape for existing consumers.
            ["Url"] = url,
            ["Evidence"] = legacyEvidence,
            ["ruleId"] = metadata.RuleId,
            ["ruleCode"] = metadata.Code,
            ["ruleVersion"] = metadata.Version,
            ["confidence"] = metadata.Confidence,
            ["detectedAt"] = detectedAt,
            ["facts"] = factList,
            ["why"] = why ?? WhyText(metadata, evidence),
            ["expected"] = evidence?.Expected
        };
        if (extra is not null) payload["extra"] = extra;
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static Dictionary<string, object?> Fact(string field, string? value, EvidenceSource source, string? expected, string? extra) =>
        new()
        {
            ["field"] = field,
            ["value"] = value,
            ["source"] = SourceName(source),
            ["expected"] = expected,
            ["length"] = value?.Length,
            ["extra"] = extra
        };

    private static string WhyText(SeoRuleMetadata metadata, RuleEvidence? evidence) =>
        evidence is null
            ? $"Deterministic rule {metadata.Code} evaluated stored crawl evidence."
            : $"Deterministic rule {metadata.Code}: observed {evidence.Field} = {evidence.Actual ?? "<empty>"}, expected {evidence.Expected}.";
}

/// <summary>
/// Configurable heuristic thresholds. These are diagnostic heuristics, NOT ranking
/// factors or Google rules: e.g. a title length band is a readability/serp-truncation
/// heuristic and word count is a content diagnostic only. Defaults preserve the
/// historical behaviour of the rules.
/// </summary>
public sealed record SeoThresholds
{
    public static readonly SeoThresholds Default = new();

    /// <summary>Heuristic: titles below this are usually uninformative (SERP truncation band).</summary>
    public int TitleMinLength { get; init; } = 20;
    /// <summary>Heuristic: titles above this are usually truncated in search results.</summary>
    public int TitleMaxLength { get; init; } = 65;
    /// <summary>Heuristic description length band.</summary>
    public int MetaDescriptionMinLength { get; init; } = 70;
    public int MetaDescriptionMaxLength { get; init; } = 170;
    /// <summary>Diagnostic only — never a ranking factor.</summary>
    public int LowWordCount { get; init; } = 250;
    public int ThinContentWordCount { get; init; } = 150;
    public int ThinContentMinWordsForRatio { get; init; } = 100;
    public long SlowResponseMs { get; init; } = 1500;
    public int DeepClickDepth { get; init; } = 3;
    public int MaxInternalLinksPerPage { get; init; } = 3000;
    public int MaxUrlLength { get; init; } = 120;
    public int MaxPathSegments { get; init; } = 6;
    public int MaxTitleLengthForTemplateWindow { get; init; } = 4;
}

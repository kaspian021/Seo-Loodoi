using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

/// <summary>
/// Central metadata registry for every deterministic SEO rule (P1 Phase 1).
/// Codes are the persisted stable ids (SeoIssue.RuleCode); RuleIds are stable dotted
/// documentation keys. The catalog is the single place that maps a rule to its
/// category, severity, resource type, prerequisites, evidence sources, confidence,
/// scoring weight, remediation key and version — so the engine can grow to hundreds
/// of rules without a conditional monolith.
/// A contract test asserts: every registered ISeoRule has an entry here, every entry
/// maps to a live code, and no id or code is duplicated.
/// </summary>
public static class SeoRuleCatalog
{
    private static readonly IReadOnlyDictionary<string, SeoRuleMetadata> ByCodeField =
        AllRules().ToDictionary(x => x.Code, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, SeoRuleMetadata> ByRuleIdField =
        AllRules().ToDictionary(x => x.RuleId, StringComparer.Ordinal);

    public static IReadOnlyList<SeoRuleMetadata> All { get; } = AllRules().ToArray();

    public static SeoRuleMetadata Get(string code) =>
        ByCodeField.TryGetValue(code, out var metadata)
            ? metadata
            : SeoRuleMetadata.Create("SEO.TECH.UNREGISTERED." + code, code, IssueCategory.Technical, IssueSeverity.Medium,
                RuleResourceType.Page, ScoreDimension.Technical, [EvidenceSource.RawHtml],
                recommendationKey: "REC_" + code, docKey: "DOC_" + code, confidence: 0.5m, version: "0.0.0");

    public static bool TryGetRuleId(string ruleId, out SeoRuleMetadata? metadata) => ByRuleIdField.TryGetValue(ruleId, out metadata);

    private static IEnumerable<SeoRuleMetadata> AllRules()
    {
        // ---- On-page metadata (Title / Meta description) ----
        yield return SeoRuleMetadata.Create("SEO.TECH.TITLE.MISSING", "TITLE_MISSING", IssueCategory.OnPage, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_TITLE_MISSING", "DOC_TITLE");
        yield return SeoRuleMetadata.Create("SEO.TECH.TITLE.LENGTH_HEURISTIC", "TITLE_LENGTH", IssueCategory.OnPage, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_TITLE_LENGTH", "DOC_TITLE", confidence: 0.7m);
        yield return SeoRuleMetadata.Create("SEO.TECH.TITLE.DUPLICATE", "DUPLICATE_TITLE_TAG", IssueCategory.OnPage, IssueSeverity.High,
            RuleResourceType.Site, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_DUPLICATE_TITLE", "DOC_TITLE",
            prerequisites: "site-pass:titles");
        yield return SeoRuleMetadata.Create("SEO.TECH.META_DESCRIPTION.MISSING", "META_DESCRIPTION_MISSING", IssueCategory.OnPage, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_META_DESCRIPTION_MISSING", "DOC_META_DESCRIPTION");
        yield return SeoRuleMetadata.Create("SEO.TECH.META_DESCRIPTION.LENGTH_HEURISTIC", "META_DESCRIPTION_LENGTH", IssueCategory.OnPage, IssueSeverity.Low,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_META_DESCRIPTION_LENGTH", "DOC_META_DESCRIPTION", confidence: 0.7m);

        // ---- Headings ----
        yield return SeoRuleMetadata.Create("SEO.TECH.H1.MISSING", "H1_MISSING", IssueCategory.OnPage, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_H1_MISSING", "DOC_HEADINGS");
        yield return SeoRuleMetadata.Create("SEO.TECH.H1.MULTIPLE", "MULTIPLE_H1", IssueCategory.OnPage, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_MULTIPLE_H1", "DOC_HEADINGS");
        yield return SeoRuleMetadata.Create("SEO.TECH.HEADING.HIERARCHY", "HEADING_STRUCTURE", IssueCategory.OnPage, IssueSeverity.Low,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_HEADING_STRUCTURE", "DOC_HEADINGS", confidence: 0.8m);

        // ---- Indexability ----
        yield return SeoRuleMetadata.Create("SEO.TECH.CANONICAL.MISSING", "CANONICAL_MISSING", IssueCategory.Indexability, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.Indexability, [EvidenceSource.RawHtml], "REC_CANONICAL_MISSING", "DOC_CANONICAL", indexabilityImpact: true);
        yield return SeoRuleMetadata.Create("SEO.TECH.CANONICAL.INVALID", "CANONICAL_INVALID", IssueCategory.Indexability, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.Indexability, [EvidenceSource.RawHtml], "REC_CANONICAL_INVALID", "DOC_CANONICAL", indexabilityImpact: true);
        yield return SeoRuleMetadata.Create("SEO.TECH.CANONICAL.MISMATCH", "CANONICAL_MISMATCH", IssueCategory.Indexability, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.Indexability, [EvidenceSource.RawHtml], "REC_CANONICAL_MISMATCH", "DOC_CANONICAL", indexabilityImpact: true);
        yield return SeoRuleMetadata.Create("SEO.TECH.ROBOT.NOINDEX", "NOINDEX", IssueCategory.Indexability, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.Indexability, [EvidenceSource.RawHtml], "REC_NOINDEX", "DOC_INDEXABILITY", indexabilityImpact: true);
        yield return SeoRuleMetadata.Create("SEO.TECH.ROBOT.X_NOINDEX", "X_ROBOTS_NOINDEX", IssueCategory.Indexability, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.Indexability, [EvidenceSource.Headers], "REC_X_ROBOTS_NOINDEX", "DOC_INDEXABILITY", indexabilityImpact: true);

        // ---- Content (diagnostics only — never ranking factors) ----
        yield return SeoRuleMetadata.Create("SEO.TECH.CONTENT.LOW_WORD_COUNT", "LOW_WORD_COUNT", IssueCategory.Content, IssueSeverity.Low,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_LOW_WORD_COUNT", "DOC_CONTENT", confidence: 0.6m);
        yield return SeoRuleMetadata.Create("SEO.TECH.CONTENT.THIN", "THIN_CONTENT", IssueCategory.Content, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_THIN_CONTENT", "DOC_CONTENT", confidence: 0.7m);
        yield return SeoRuleMetadata.Create("SEO.TECH.CONTENT.KEYWORD_STUFFING", "KEYWORD_STUFFING", IssueCategory.Content, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_KEYWORD_STUFFING", "DOC_CONTENT", confidence: 0.8m);
        yield return SeoRuleMetadata.Create("SEO.TECH.CONTENT.LONG_SENTENCES", "CONTENT_LONG_SENTENCES", IssueCategory.Content, IssueSeverity.Low,
            RuleResourceType.Page, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_LONG_SENTENCES", "DOC_CONTENT", confidence: 0.7m);
        yield return SeoRuleMetadata.Create("SEO.TECH.CONTENT.DUPLICATE_EXACT", "DUPLICATE_CONTENT", IssueCategory.Content, IssueSeverity.Medium,
            RuleResourceType.Cluster, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_DUPLICATE_CONTENT", "DOC_DUPLICATES",
            prerequisites: "site-pass:content-similarity");
        yield return SeoRuleMetadata.Create("SEO.TECH.CONTENT.DUPLICATE_NEAR", "NEAR_DUPLICATE_CONTENT", IssueCategory.Content, IssueSeverity.Medium,
            RuleResourceType.Cluster, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_NEAR_DUPLICATE_CONTENT", "DOC_DUPLICATES",
            confidence: 0.8m, prerequisites: "site-pass:content-similarity");

        // ---- Images ----
        yield return SeoRuleMetadata.Create("SEO.TECH.IMAGE.ALT_MISSING", "ALT_MISSING", IssueCategory.OnPage, IssueSeverity.Low,
            RuleResourceType.Asset, ScoreDimension.ContentTechnical, [EvidenceSource.RawHtml], "REC_ALT_MISSING", "DOC_IMAGES", confidence: 0.8m);
        yield return SeoRuleMetadata.Create("SEO.TECH.IMAGE.DIMENSIONS_MISSING", "IMAGE_DIMENSIONS_MISSING", IssueCategory.Performance, IssueSeverity.Low,
            RuleResourceType.Asset, ScoreDimension.Performance, [EvidenceSource.RawHtml], "REC_IMAGE_DIMENSIONS", "DOC_IMAGES", confidence: 0.8m);

        // ---- Performance ----
        yield return SeoRuleMetadata.Create("SEO.TECH.PERF.SLOW_RESPONSE", "SLOW_RESPONSE", IssueCategory.Performance, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.Performance, [EvidenceSource.Http], "REC_SLOW_RESPONSE", "DOC_PERFORMANCE");
        yield return SeoRuleMetadata.Create("SEO.TECH.PERF.RENDER_BLOCKING", "RENDER_BLOCKING_RESOURCES", IssueCategory.Performance, IssueSeverity.Medium,
            RuleResourceType.Asset, ScoreDimension.Performance, [EvidenceSource.RawHtml], "REC_RENDER_BLOCKING", "DOC_PERFORMANCE", confidence: 0.8m);
        yield return SeoRuleMetadata.Create("SEO.TECH.PERF.EXCESSIVE_RESOURCES", "EXCESSIVE_RESOURCES", IssueCategory.Performance, IssueSeverity.Low,
            RuleResourceType.Asset, ScoreDimension.Performance, [EvidenceSource.RawHtml], "REC_EXCESSIVE_RESOURCES", "DOC_PERFORMANCE", confidence: 0.7m);

        // ---- HTTP / URL ----
        yield return SeoRuleMetadata.Create("SEO.TECH.HTTP.BROKEN", "BROKEN_STATUS", IssueCategory.Technical, IssueSeverity.Medium,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Http], "REC_BROKEN_STATUS", "DOC_HTTP",
            requiresHtmlSnapshot: false);
        yield return SeoRuleMetadata.Create("SEO.TECH.HTTP.REDIRECTED", "REDIRECTED_PAGE", IssueCategory.Technical, IssueSeverity.Notice,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Http], "REC_REDIRECTED_PAGE", "DOC_HTTP",
            requiresHtmlSnapshot: false);
        yield return SeoRuleMetadata.Create("SEO.TECH.HTTP.REDIRECT_CHAIN_LONG", "REDIRECT_CHAIN_LONG", IssueCategory.Technical, IssueSeverity.Medium,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Http], "REC_REDIRECT_CHAIN", "DOC_HTTP",
            requiresHtmlSnapshot: false, confidence: 0.9m);
        yield return SeoRuleMetadata.Create("SEO.TECH.HTTP.CONTENT_TYPE_MISSING", "CONTENT_TYPE_MISSING", IssueCategory.Technical, IssueSeverity.Low,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Headers], "REC_CONTENT_TYPE_MISSING", "DOC_HTTP",
            requiresHtmlSnapshot: false);
        yield return SeoRuleMetadata.Create("SEO.TECH.HTTP.CACHE_CONTROL_MISSING", "CACHE_CONTROL_MISSING", IssueCategory.Technical, IssueSeverity.Low,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Headers], "REC_CACHE_CONTROL", "DOC_HTTP",
            requiresHtmlSnapshot: false, confidence: 0.8m);
        yield return SeoRuleMetadata.Create("SEO.TECH.HTTP.HTTPS_ISSUE", "HTTPS_ISSUE", IssueCategory.Security, IssueSeverity.High,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Http], "REC_HTTPS_ISSUE", "DOC_SECURITY",
            requiresHtmlSnapshot: false, indexabilityImpact: true);

        // ---- Security ----
        yield return SeoRuleMetadata.Create("SEO.TECH.SECURITY.HSTS_MISSING", "HSTS_MISSING", IssueCategory.Security, IssueSeverity.Medium,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Headers], "REC_HSTS_MISSING", "DOC_SECURITY",
            requiresHtmlSnapshot: false);
        yield return SeoRuleMetadata.Create("SEO.TECH.SECURITY.HEADERS_MISSING", "SECURITY_HEADERS_MISSING", IssueCategory.Security, IssueSeverity.Low,
            RuleResourceType.Url, ScoreDimension.Technical, [EvidenceSource.Headers], "REC_SECURITY_HEADERS", "DOC_SECURITY",
            requiresHtmlSnapshot: false, confidence: 0.8m);
        yield return SeoRuleMetadata.Create("SEO.TECH.SECURITY.MIXED_CONTENT", "MIXED_CONTENT_ASSETS", IssueCategory.Security, IssueSeverity.High,
            RuleResourceType.Asset, ScoreDimension.Technical, [EvidenceSource.RawHtml], "REC_MIXED_CONTENT", "DOC_SECURITY");

        // ---- Internal links / architecture ----
        yield return SeoRuleMetadata.Create("SEO.TECH.LINK.DEEP_CLICK_DEPTH", "DEEP_CLICK_DEPTH", IssueCategory.Technical, IssueSeverity.Medium,
            RuleResourceType.Url, ScoreDimension.Architecture, [EvidenceSource.Links], "REC_DEEP_CLICK_DEPTH", "DOC_LINKS",
            requiresHtmlSnapshot: false);
        yield return SeoRuleMetadata.Create("SEO.TECH.LINK.ORPHAN", "ORPHAN_PAGE", IssueCategory.InternalLinks, IssueSeverity.High,
            RuleResourceType.Site, ScoreDimension.Architecture, [EvidenceSource.Links], "REC_ORPHAN_PAGE", "DOC_LINKS",
            prerequisites: "site-pass:link-graph");
        yield return SeoRuleMetadata.Create("SEO.TECH.LINK.DEAD_END", "DEAD_END_PAGE", IssueCategory.InternalLinks, IssueSeverity.Medium,
            RuleResourceType.Site, ScoreDimension.Architecture, [EvidenceSource.Links], "REC_DEAD_END_PAGE", "DOC_LINKS",
            prerequisites: "site-pass:link-graph");
        yield return SeoRuleMetadata.Create("SEO.TECH.LINK.ANCHOR_GENERIC", "GENERIC_ANCHOR_TEXT", IssueCategory.InternalLinks, IssueSeverity.Low,
            RuleResourceType.Page, ScoreDimension.Architecture, [EvidenceSource.Links], "REC_GENERIC_ANCHOR", "DOC_LINKS",
            prerequisites: "site-pass:link-graph", confidence: 0.8m);

        // ---- International (hreflang) ----
        yield return SeoRuleMetadata.Create("SEO.TECH.HREFLANG.NO_SELF_REFERENCE", "HREFLANG_NO_SELF_REFERENCE", IssueCategory.International, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.International, [EvidenceSource.RawHtml], "REC_HREFLANG_NO_SELF", "DOC_HREFLANG");
        yield return SeoRuleMetadata.Create("SEO.TECH.HREFLANG.INVALID_CODE", "HREFLANG_INVALID_CODE", IssueCategory.International, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.International, [EvidenceSource.RawHtml], "REC_HREFLANG_INVALID_CODE", "DOC_HREFLANG");

        // ---- Structured data ----
        yield return SeoRuleMetadata.Create("SEO.TECH.SCHEMA.ABSENT", "STRUCTURED_DATA_ABSENT", IssueCategory.StructuredData, IssueSeverity.Notice,
            RuleResourceType.Page, ScoreDimension.StructuredData, [EvidenceSource.StructuredData], "REC_SCHEMA_ABSENT", "DOC_STRUCTURED_DATA");
        yield return SeoRuleMetadata.Create("SEO.TECH.SCHEMA.SYNTAX_INVALID", "SCHEMA_SYNTAX_INVALID", IssueCategory.StructuredData, IssueSeverity.Medium,
            RuleResourceType.Page, ScoreDimension.StructuredData, [EvidenceSource.StructuredData], "REC_SCHEMA_SYNTAX", "DOC_STRUCTURED_DATA");
        yield return SeoRuleMetadata.Create("SEO.TECH.SCHEMA.MISSING_REQUIRED", "SCHEMA_MISSING_REQUIRED", IssueCategory.StructuredData, IssueSeverity.Low,
            RuleResourceType.Page, ScoreDimension.StructuredData, [EvidenceSource.StructuredData], "REC_SCHEMA_MISSING_REQUIRED", "DOC_STRUCTURED_DATA",
            confidence: 0.8m);

        // ---- JavaScript SEO (consumes stored render evidence; never recrawls) ----
        yield return SeoRuleMetadata.Create("SEO.TECH.JS.RENDER_MISMATCH", "JS_RENDER_MISMATCH", IssueCategory.Indexability, IssueSeverity.High,
            RuleResourceType.Page, ScoreDimension.JavaScriptSeo, [EvidenceSource.RenderedDom, EvidenceSource.RawHtml], "REC_JS_RENDER_MISMATCH", "DOC_JS_SEO",
            requiresHtmlSnapshot: false, indexabilityImpact: true, prerequisites: "site-pass:render-evidence");
    }
}

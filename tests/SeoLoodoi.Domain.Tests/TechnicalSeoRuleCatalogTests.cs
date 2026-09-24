using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Infrastructure;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// P1 Phase 1/2 contract tests: every rule has explicit, stable metadata; the
/// evidence envelope is machine-readable and keeps the legacy keys; the HTML-snapshot
/// gate comes from metadata, not from a hard-coded code list.
/// </summary>
public class TechnicalSeoRuleCatalogTests
{
    /// <summary>Codes emitted by the analysis pipeline instead of an ISeoRule instance.</summary>
    private static readonly string[] PipelineCodes =
    [
        "JS_RENDER_MISMATCH", "DUPLICATE_CONTENT", "NEAR_DUPLICATE_CONTENT",
        "ORPHAN_PAGE", "DEAD_END_PAGE", "GENERIC_ANCHOR_TEXT", "DUPLICATE_TITLE_TAG"
    ];

    private static IReadOnlyList<ISeoRule> RegisteredRules()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseProvider"] = "InMemory"
        }).Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(config);
        // Resolve through the production container so rules with optional-ctor
        // thresholds are constructed exactly as the analysis pipeline does.
        using var provider = services.BuildServiceProvider();
        return provider.GetServices<ISeoRule>().ToArray();
    }

    [Fact]
    public void EveryRegisteredRule_HasCompleteCatalogMetadata()
    {
        foreach (var rule in RegisteredRules())
        {
            var meta = SeoRuleCatalog.Get(rule.Code);
            meta.Code.Should().Be(rule.Code);
            meta.Version.Should().NotBeNullOrWhiteSpace();
            meta.RuleId.Should().StartWith("SEO.");
            meta.DocKey.Should().NotBeNullOrWhiteSpace();
            meta.RecommendationKey.Should().NotBeNullOrWhiteSpace();
            meta.TitleKey.Should().NotBeNullOrWhiteSpace();
            meta.DescriptionKey.Should().NotBeNullOrWhiteSpace();
            meta.EvidenceSources.Should().NotBeEmpty();
            meta.Confidence.Should().BeGreaterThan(0m).And.BeLessThanOrEqualTo(1m);
            meta.ScoringWeight.Should().BeGreaterThan(0m);
        }
    }

    [Fact]
    public void CatalogIds_AreStableUniqueMachineKeys()
    {
        SeoRuleCatalog.All.Select(x => x.RuleId).Should().OnlyHaveUniqueItems();
        SeoRuleCatalog.All.Select(x => x.Code).Should().OnlyHaveUniqueItems();
        foreach (var meta in SeoRuleCatalog.All)
        {
            // Identity is a machine key, never display text.
            meta.RuleId.Should().MatchRegex(@"^SEO\.[A-Z0-9_]+(\.[A-Z0-9_]+)+$");
            meta.Code.Should().MatchRegex(@"^[A-Z][A-Z0-9_]*$");
        }
    }

    [Fact]
    public void PipelineCodes_AreRegisteredInTheCatalog()
    {
        foreach (var code in PipelineCodes)
        {
            var meta = SeoRuleCatalog.Get(code);
            meta.Code.Should().Be(code, "pipeline-emitted issues must carry rule metadata too");
            meta.Prerequisites.Should().StartWith("site-pass");
        }
    }

    [Fact]
    public void HtmlSnapshotGate_ComesFromMetadata_NotAHardCodedList()
    {
        string[] transportRules =
        [
            "BROKEN_STATUS", "REDIRECTED_PAGE", "CONTENT_TYPE_MISSING", "HTTPS_ISSUE", "REDIRECT_CHAIN_LONG",
            "HSTS_MISSING", "SECURITY_HEADERS_MISSING", "CACHE_CONTROL_MISSING", "DEEP_CLICK_DEPTH"
        ];
        foreach (var rule in RegisteredRules())
        {
            var expected = !transportRules.Contains(rule.Code);
            SeoRuleCatalog.Get(rule.Code).RequiresHtmlSnapshot.Should().Be(expected,
                $"{rule.Code} must declare its HTML-snapshot prerequisite explicitly");
        }
    }

    [Fact]
    public void EvidenceEnvelope_IsMachineReadable_AndKeepsLegacyKeys()
    {
        var meta = SeoRuleCatalog.Get("TITLE_MISSING");
        var evidence = new RuleEvidence("title", "", "A unique, descriptive title");
        var json = SeoEvidence.Build("https://example.com/x", meta, evidence, DateTimeOffset.Parse("2026-09-25T08:00:00+00:00"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("Url").GetString().Should().Be("https://example.com/x");
        root.GetProperty("Evidence").GetProperty("Field").GetString().Should().Be("title");
        root.GetProperty("Evidence").GetProperty("Actual").GetString().Should().Be("");
        root.GetProperty("ruleId").GetString().Should().Be("SEO.TECH.TITLE.MISSING");
        root.GetProperty("ruleCode").GetString().Should().Be("TITLE_MISSING");
        root.GetProperty("ruleVersion").GetString().Should().Be(SeoRuleMetadata.CurrentVersion);
        root.GetProperty("confidence").GetDecimal().Should().Be(1m);
        root.GetProperty("detectedAt").GetDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-09-25T08:00:00+00:00"));
        root.GetProperty("expected").GetString().Should().Be("A unique, descriptive title");
        root.GetProperty("why").GetString().Should().Contain("TITLE_MISSING");

        var fact = root.GetProperty("facts").EnumerateArray().Single();
        fact.GetProperty("field").GetString().Should().Be("title");
        fact.GetProperty("value").GetString().Should().Be("");
        fact.GetProperty("length").GetInt32().Should().Be(0);
        fact.GetProperty("source").GetString().Should().Be("raw-html");
    }

    [Fact]
    public void EvidenceEnvelope_KeepsUnderlyingFacts_ForNonPageRules()
    {
        var meta = SeoRuleCatalog.Get("JS_RENDER_MISMATCH");
        var json = SeoEvidence.Build("https://example.com/spa", meta,
            new RuleEvidence("renderedVsRaw", "title,canonical", "identical"), DateTimeOffset.UtcNow,
            extra: new { criticalDifferences = 2 },
            facts: [new EvidenceFact("criticalDifferences", "2", EvidenceSource.RenderedDom, "0")]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("extra").GetProperty("criticalDifferences").GetInt32().Should().Be(2);
        var facts = root.GetProperty("facts").EnumerateArray().ToArray();
        facts.Length.Should().Be(2);
        facts[0].GetProperty("source").GetString().Should().Be("rendered-dom");
        facts[1].GetProperty("field").GetString().Should().Be("criticalDifferences");
    }

    [Fact]
    public void SourceNames_AreStableKebabCase()
    {
        SeoEvidence.SourceName(EvidenceSource.RawHtml).Should().Be("raw-html");
        SeoEvidence.SourceName(EvidenceSource.RenderedDom).Should().Be("rendered-dom");
        SeoEvidence.SourceName(EvidenceSource.Http).Should().Be("http");
        SeoEvidence.SourceName(EvidenceSource.Headers).Should().Be("headers");
        SeoEvidence.SourceName(EvidenceSource.Robots).Should().Be("robots");
        SeoEvidence.SourceName(EvidenceSource.Sitemap).Should().Be("sitemap");
        SeoEvidence.SourceName(EvidenceSource.Links).Should().Be("links");
        SeoEvidence.SourceName(EvidenceSource.StructuredData).Should().Be("structured-data");
        SeoEvidence.SourceName(EvidenceSource.SearchConsole).Should().Be("search-console");
        SeoEvidence.SourceName(EvidenceSource.PerformanceProvider).Should().Be("performance-provider");
        SeoEvidence.SourceName(EvidenceSource.ExternalProvider).Should().Be("external-provider");
    }

    [Fact]
    public void UnknownCodes_StillProduceAnEvidenceEnvelope_WithoutLyingAboutMetadata()
    {
        var meta = SeoRuleCatalog.Get("NOT_A_REAL_CODE");
        meta.Version.Should().Be("0.0.0", "an unregistered code must not claim a stable rule version");
        meta.Confidence.Should().Be(0.5m);
    }
}

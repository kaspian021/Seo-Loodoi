using System.Text.Json;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Infrastructure.Crawling;

namespace SeoLoodoi.Domain.Tests;

public sealed class AeoIssueRulesTests
{
    private static AiVisibilityReportDto Analyze(string? headings, string? schema, string? text = "Observed page") =>
        new AeoAnalyzer(new RobotsParser()).Analyze(Guid.NewGuid(), Guid.NewGuid(), null, new Uri("https://example.com"), [],
            [new AeoPageInput("https://example.com/guide", text, schema, headings, null, 200)]);

    [Theory]
    [InlineData(null, "[]")]
    [InlineData("[]", null)]
    [InlineData("not-json", "[]")]
    [InlineData("[]", "[\"not-json\"]")]
    [InlineData("[null]", "[]")]
    public void IncompleteEvidence_DoesNotInventMissingAnswerStructure(string? headings, string? schema)
    {
        var report = Analyze(headings, schema);
        Assert.False(AeoIssueRules.WasEvaluated(AeoIssueRules.AnswerStructure, report));
        Assert.Empty(AeoIssueRules.Evaluate(report, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MeasuredEmptyStructure_ProducesSampleScopedAdvisory_NotAVisibilityPrediction()
    {
        var report = Analyze("[]", "[]");
        var finding = Assert.Single(AeoIssueRules.Evaluate(report, DateTimeOffset.UtcNow));
        Assert.Equal(AeoIssueRules.AnswerStructure, finding.Code);
        Assert.Contains("sample", finding.EvidenceJson);
        Assert.Null(report.AiVisibilityScore);
    }

    [Fact]
    public async Task RealExtractorSerialization_PreservesQuestionFaqAndAuthorshipSignals()
    {
        const string html = """
            <html><head><title>Guide</title><script type="application/ld+json">
            {"@context":"https://schema.org","@type":"FAQPage","author":{"@type":"Person","name":"Author"}}
            </script></head><body><h1>What is SEO?</h1><h2>Answer</h2><h3>Details</h3><p>Observed text.</p></body></html>
            """;
        var extracted = await new HtmlExtractor().ExtractAsync(html, new Uri("https://example.com/guide"));
        var report = Analyze(JsonSerializer.Serialize(extracted.Headings), JsonSerializer.Serialize(extracted.JsonLd), extracted.Text);
        Assert.Equal(1, report.Signals.PagesWithQuestionHeadings);
        Assert.Equal(1, report.Signals.PagesWithHeadingStructure);
        Assert.Equal(1, report.Signals.PagesWithFaqSchema);
        Assert.Equal(1, report.Signals.PagesWithAuthorOrDate);
        Assert.Equal(1, report.Signals.PagesWithEvaluableAnswerStructure);
        Assert.Empty(AeoIssueRules.Evaluate(report, DateTimeOffset.UtcNow));
    }
}

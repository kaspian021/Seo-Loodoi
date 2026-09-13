using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// Rule-level regressions. Every triggered issue becomes customer-visible Persian
/// guidance, so a rule must fire on the exact condition its code name promises.
/// </summary>
public sealed class NoIndexRuleTests
{
    private static PageAnalysisContext Context(int statusCode, bool isIndexable, string? robotsMeta = null, string? xRobotsTag = null) =>
        new("https://example.com/page", "Title", "Meta", ["H1"], "https://example.com/page",
            500, 1, 0, 120, isIndexable, [1, 2], statusCode, "text/html", xRobotsTag, robotsMeta);

    [Fact]
    public void BrokenPage_IsNotReportedAsNoIndex()
    {
        // A 404 page is not indexable because of its status, not because of a
        // noindex directive. Reporting NOINDEX for it is factually wrong and
        // double-counts with BROKEN_STATUS.
        var context = Context(statusCode: 404, isIndexable: false, robotsMeta: null);

        var result = new NoIndexRule().Evaluate(context);

        result.Triggered.Should().BeFalse();
    }

    [Fact]
    public void MetaNoIndex_OnSuccessfulPage_Triggers()
    {
        var context = Context(statusCode: 200, isIndexable: false, robotsMeta: "noindex, follow");

        var result = new NoIndexRule().Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.High);
        result.Category.Should().Be(IssueCategory.Indexability);
    }

    [Fact]
    public void IndexablePage_DoesNotTrigger()
    {
        var context = Context(statusCode: 200, isIndexable: true, robotsMeta: "index, follow");

        new NoIndexRule().Evaluate(context).Triggered.Should().BeFalse();
    }

    [Fact]
    public void BrokenStatusRule_StillFires_For404()
    {
        var context = Context(statusCode: 404, isIndexable: false);

        var result = new BrokenStatusRule().Evaluate(context);

        result.Triggered.Should().BeTrue();
    }
}

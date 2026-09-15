using System.Text.Json;
using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public sealed class EnterpriseCrawlRulesTests
{
    [Fact]
    public void RedirectChainLongRule_Triggers_When_Hops_Exceed_Two()
    {
        var hops = new[]
        {
            new { fromUrl = "http://example.com/1", toUrl = "https://example.com/2", statusCode = 301, durationMs = 20 },
            new { fromUrl = "https://example.com/2", toUrl = "https://example.com/3", statusCode = 302, durationMs = 25 },
            new { fromUrl = "https://example.com/3", toUrl = "https://example.com/4", statusCode = 307, durationMs = 15 }
        };
        var context = new PageAnalysisContext(
            "https://example.com/4", "Title", "Meta", ["H1"], "https://example.com/4",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            JsonSerializer.Serialize(hops), "[]");

        var rule = new RedirectChainLongRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.Medium);
        result.Category.Should().Be(IssueCategory.Technical);
        result.Evidence?.Field.Should().Be("redirectHops");
        result.Evidence?.Actual.Should().Be("3");
    }

    [Fact]
    public void RedirectChainLongRule_DoesNotTrigger_When_Hops_Are_Two_Or_Fewer()
    {
        var hops = new[]
        {
            new { fromUrl = "http://example.com/1", toUrl = "https://example.com/2", statusCode = 301, durationMs = 20 }
        };
        var context = new PageAnalysisContext(
            "https://example.com/2", "Title", "Meta", ["H1"], "https://example.com/2",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            JsonSerializer.Serialize(hops), "[]");

        var rule = new RedirectChainLongRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeFalse();
    }

    [Fact]
    public void MixedContentAssetsRule_Triggers_On_Insecure_Assets_Under_Https()
    {
        var assets = new[]
        {
            new { type = "Image", url = "http://insecure.example/img.png", isMixedContent = true },
            new { type = "Script", url = "https://secure.example/app.js", isMixedContent = false }
        };
        var context = new PageAnalysisContext(
            "https://example.com/page", "Title", "Meta", ["H1"], "https://example.com/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", JsonSerializer.Serialize(assets));

        var rule = new MixedContentAssetsRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.High);
        result.Category.Should().Be(IssueCategory.Security);
        result.Evidence?.Actual.Should().Be("1");
    }

    [Fact]
    public void MixedContentAssetsRule_DoesNotTrigger_On_Http_Origin()
    {
        var assets = new[]
        {
            new { type = "Image", url = "http://insecure.example/img.png", isMixedContent = true }
        };
        var context = new PageAnalysisContext(
            "http://example.com/page", "Title", "Meta", ["H1"], "http://example.com/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", JsonSerializer.Serialize(assets));

        var rule = new MixedContentAssetsRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeFalse();
    }

    [Fact]
    public void ExcessiveResourcesRule_Triggers_When_Script_Count_Exceeds_Threshold()
    {
        var assets = Enumerable.Range(0, 30)
            .Select(i => new { type = "Script", url = $"https://example.com/js/{i}.js", isMixedContent = false })
            .ToArray();
        var context = new PageAnalysisContext(
            "https://example.com/page", "Title", "Meta", ["H1"], "https://example.com/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", JsonSerializer.Serialize(assets));

        var rule = new ExcessiveResourcesRule(scriptLimit: 25, totalLimit: 75);
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.Low);
        result.Category.Should().Be(IssueCategory.Performance);
    }
}

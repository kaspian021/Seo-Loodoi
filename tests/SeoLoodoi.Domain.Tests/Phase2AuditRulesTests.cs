using System.Text.Json;
using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase2AuditRulesTests
{
    [Fact]
    public void SchemaSyntaxRule_Triggers_On_Missing_Type()
    {
        var schemaJson = "[{\"headline\": \"Sample news without type\"}]";
        var context = new PageAnalysisContext(
            "https://example.com/news", "Title", "Meta", ["H1"], "https://example.com/news",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", schemaJson, "[]", "{}");

        var rule = new SchemaSyntaxRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.Medium);
        result.Category.Should().Be(IssueCategory.StructuredData);
    }

    [Fact]
    public void SchemaMissingRequiredRule_Triggers_On_Article_Without_Author()
    {
        var schemaJson = "[{\"@context\": \"https://schema.org\", \"@type\": \"Article\", \"headline\": \"News Title\"}]";
        var context = new PageAnalysisContext(
            "https://example.com/news", "Title", "Meta", ["H1"], "https://example.com/news",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", schemaJson, "[]", "{}");

        var rule = new SchemaMissingRequiredRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.StructuredData);
    }

    [Fact]
    public void HreflangNoSelfRule_Triggers_When_Self_Reference_Is_Missing()
    {
        var hreflang = new[]
        {
            new { language = "en", target = "https://example.com/en/page" },
            new { language = "fr", target = "https://example.com/fr/page" }
        };
        var context = new PageAnalysisContext(
            "https://example.com/fa/page", "Title", "Meta", ["H1"], "https://example.com/fa/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", JsonSerializer.Serialize(hreflang), "{}");

        var rule = new HreflangNoSelfRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.International);
    }

    [Fact]
    public void HreflangInvalidLanguageRule_Triggers_On_Malformed_Language_Tag()
    {
        var hreflang = new[]
        {
            new { language = "invalid_lang_code$$$", target = "https://example.com/page" }
        };
        var context = new PageAnalysisContext(
            "https://example.com/page", "Title", "Meta", ["H1"], "https://example.com/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", JsonSerializer.Serialize(hreflang), "{}");

        var rule = new HreflangInvalidLanguageRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.International);
    }

    [Fact]
    public void HstsMissingRule_Triggers_On_Https_Page_Without_HSTS()
    {
        var headers = new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["text/html"]
        };
        var context = new PageAnalysisContext(
            "https://example.com/secure", "Title", "Meta", ["H1"], "https://example.com/secure",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", "[]", JsonSerializer.Serialize(headers));

        var rule = new HstsMissingRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.Security);
    }

    [Fact]
    public void RenderBlockingResourcesRule_Triggers_When_Synchronous_Scripts_Exceed_Limit()
    {
        var assets = new[]
        {
            new { type = "Script", url = "https://example.com/1.js", extraAttributesJson = "{\"isAsync\":false,\"isDefer\":false}" },
            new { type = "Script", url = "https://example.com/2.js", extraAttributesJson = "{\"isAsync\":false,\"isDefer\":false}" },
            new { type = "Script", url = "https://example.com/3.js", extraAttributesJson = "{\"isAsync\":false,\"isDefer\":false}" },
            new { type = "Script", url = "https://example.com/4.js", extraAttributesJson = "{\"isAsync\":false,\"isDefer\":false}" }
        };
        var context = new PageAnalysisContext(
            "https://example.com/page", "Title", "Meta", ["H1"], "https://example.com/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", JsonSerializer.Serialize(assets), "[]", "[]", "{}");

        var rule = new RenderBlockingResourcesRule(threshold: 3);
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.Performance);
    }

    [Fact]
    public void ImageDimensionsMissingRule_Triggers_When_Image_Has_No_Width_Or_Height()
    {
        var assets = new[]
        {
            new { type = "Image", url = "https://example.com/img.png", extraAttributesJson = (string?)null }
        };
        var context = new PageAnalysisContext(
            "https://example.com/page", "Title", "Meta", ["H1"], "https://example.com/page",
            500, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", JsonSerializer.Serialize(assets), "[]", "[]", "{}");

        var rule = new ImageDimensionsMissingRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.Performance);
    }
}

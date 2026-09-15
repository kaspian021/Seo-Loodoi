using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase4ContentEngineTests
{
    [Fact]
    public void ContentQualityEngine_Calculates_Readability_And_Extracts_Keywords()
    {
        var text = "سئو تکنیکال پایه و اساس بهینه‌سازی موتورهای جستجو است. ساختار سایت باید استاندارد باشد. سرعت بارگذاری صفحات اهمیت زیادی دارد.";
        var engine = new ContentQualityEngine();
        var analysis = engine.Analyze(text, "https://example.com/seo-guide");

        analysis.Readability.WordCount.Should().BeGreaterThan(10);
        analysis.Readability.SentenceCount.Should().Be(3);
        analysis.Readability.ReadabilityScore.Should().BeGreaterThan(70m);
        analysis.Readability.ReadabilityGrade.Should().Be("Easy");
        analysis.HasKeywordStuffing.Should().BeFalse();
    }

    [Fact]
    public void ContentQualityEngine_Detects_Keyword_Stuffing()
    {
        // 10 times 'تخفیف' in a 30-word text -> ~33% density!
        var text = "تخفیف ویژه برای خرید آنلاین. تخفیف کفش و تخفیف لباس. بهترین تخفیف فصل را با تخفیف ما تجربه کنید. تخفیف روزانه و تخفیف هفتگی همراه با تخفیف شگفت‌انگیز و تخفیف بهاره.";
        var engine = new ContentQualityEngine();
        var analysis = engine.Analyze(text, "https://example.com/discounts");

        analysis.HasKeywordStuffing.Should().BeTrue();
        var stuffingKeyword = analysis.TopKeywords.FirstOrDefault(x => x.IsStuffing);
        stuffingKeyword.Should().NotBeNull();
        stuffingKeyword!.Term.Should().Be("تخفیف");
        stuffingKeyword.DensityPercentage.Should().BeGreaterThan(3.5m);
    }

    [Fact]
    public void KeywordStuffingRule_Triggers_On_Stuffed_Content()
    {
        var text = "خرید گوشی ارزان با بهترین قیمت. خرید گوشی سامسونگ و خرید گوشی شیائومی. مرکز خرید گوشی و تخفیف خرید گوشی در تهران.";
        var context = new PageAnalysisContext(
            "https://example.com/phones", "خرید گوشی", "Meta", ["خرید گوشی"], "https://example.com/phones",
            120, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", "[]", "{}", Depth: 1, TextContent: text);

        var rule = new KeywordStuffingRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Severity.Should().Be(IssueSeverity.High);
        result.Category.Should().Be(IssueCategory.Content);
    }

    [Fact]
    public void ThinContentRule_Triggers_When_WordCount_Is_Under_Threshold()
    {
        var context = new PageAnalysisContext(
            "https://example.com/thin", "Thin Page", "Meta", ["Thin"], "https://example.com/thin",
            45, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", "[]", "{}", Depth: 1, TextContent: "Short text with very few words.");

        var rule = new ThinContentRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeTrue();
        result.Category.Should().Be(IssueCategory.Content);
        result.Severity.Should().Be(IssueSeverity.Medium);
        result.Evidence?.Actual.Should().Be("45");
    }

    [Fact]
    public void ThinContentRule_DoesNotTrigger_On_Rich_Content()
    {
        var context = new PageAnalysisContext(
            "https://example.com/rich", "Rich Page", "Meta", ["Rich"], "https://example.com/rich",
            450, 1, 0, 120, true, null, 200, "text/html", null, null,
            "[]", "[]", "[]", "[]", "{}", Depth: 1, TextContent: "Long article with rich content...");

        var rule = new ThinContentRule();
        var result = rule.Evaluate(context);

        result.Triggered.Should().BeFalse();
    }
}

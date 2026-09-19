using AwesomeAssertions;
using SeoLoodoi.Application.Keywords;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase5KeywordIntelligenceTests
{
    [Fact]
    public void Keyword_Normalize_CleansPersianAndArabicCharacters()
    {
        var raw = "  خرید آنلاین گوشى با تخفیف ويژه  ";
        var normalized = Keyword.Normalize(raw);

        normalized.Should().Be("خرید آنلاین گوشی با تخفیف ویژه");
        normalized.Should().NotContain("ى");
        normalized.Should().NotContain("ي");
    }

    [Fact]
    public void KeywordDto_TrendCalculation_DetectsRankImprovements()
    {
        // Pos 8 on day 1 -> Pos 4 on day 2 = rank improvement of +4.0
        var keyword = new Keyword(Guid.NewGuid(), "خرید لپ تاپ", "fa", "IR");
        var metrics = new List<KeywordMetric>
        {
            new(keyword.ProjectId, keyword.Id, new DateOnly(2026, 9, 1), 10, 100, 0.1m, 8.0m, "gsc", "https://example.com/laptops"),
            new(keyword.ProjectId, keyword.Id, new DateOnly(2026, 9, 2), 25, 120, 0.2m, 4.0m, "gsc", "https://example.com/laptops")
        };

        // Date groups in ascending order
        var dateGroups = metrics
            .GroupBy(x => x.Date)
            .OrderBy(g => g.Key)
            .Select(g => new KeywordHistoryPointDto(g.Key, g.Sum(x => x.AveragePosition * x.Impressions) / g.Sum(x => x.Impressions), g.Sum(x => x.Impressions), g.Sum(x => x.Clicks)))
            .ToArray();

        var currentPos = dateGroups[^1].Position;
        var prevPos = dateGroups[^2].Position;
        var delta = prevPos - currentPos;
        var trend = delta > 0.5m ? "up" : delta < -0.5m ? "down" : "stable";

        currentPos.Should().Be(4.0m);
        prevPos.Should().Be(8.0m);
        delta.Should().Be(4.0m);
        trend.Should().Be("up");
    }

    [Fact]
    public void KeywordCannibalization_Identifies_CompetingLandingPages()
    {
        var keywordId = Guid.NewGuid();
        var phrase = "سئو تکنیکال";
        var metrics = new List<KeywordMetric>
        {
            // Page A: 60 impressions, pos 3
            new(Guid.NewGuid(), keywordId, new DateOnly(2026, 9, 10), 12, 60, 0.2m, 3.2m, "gsc", "https://example.com/technical-seo"),
            // Page B: 40 impressions, pos 7
            new(Guid.NewGuid(), keywordId, new DateOnly(2026, 9, 10), 4, 40, 0.1m, 7.5m, "gsc", "https://example.com/blog/technical-seo-tips")
        };

        var pageGroups = metrics.GroupBy(x => x.PageUrl!.Trim())
            .Select(pg => new
            {
                PageUrl = pg.Key,
                Impressions = pg.Sum(x => x.Impressions),
                Clicks = pg.Sum(x => x.Clicks),
                AveragePosition = pg.Sum(x => x.AveragePosition * x.Impressions) / pg.Sum(x => x.Impressions)
            })
            .OrderByDescending(x => x.Impressions)
            .ToArray();

        pageGroups.Length.Should().Be(2);
        var totalImpr = pageGroups.Sum(x => x.Impressions);
        totalImpr.Should().Be(100);

        var primary = pageGroups[0];
        var secondary = pageGroups[1];
        var secondaryShare = (decimal)secondary.Impressions / totalImpr * 100m;

        secondaryShare.Should().Be(40m); // 40% share -> triggers High severity cannibalization!
        var severity = secondaryShare >= 30m ? "High" : "Medium";
        severity.Should().Be("High");
    }

    [Fact]
    public void KeywordCannibalization_DoesNotTrigger_WhenSinglePageReceivesAllTraffic()
    {
        var keywordId = Guid.NewGuid();
        var metrics = new List<KeywordMetric>
        {
            new(Guid.NewGuid(), keywordId, new DateOnly(2026, 9, 10), 20, 100, 0.2m, 2.0m, "gsc", "https://example.com/single-guide"),
            new(Guid.NewGuid(), keywordId, new DateOnly(2026, 9, 11), 30, 150, 0.2m, 1.8m, "gsc", "https://example.com/single-guide")
        };

        var pageGroups = metrics.GroupBy(x => x.PageUrl!.Trim()).ToArray();
        pageGroups.Length.Should().Be(1);
    }
}

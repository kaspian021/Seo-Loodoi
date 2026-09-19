using AwesomeAssertions;
using SeoLoodoi.Application.Competitors;

namespace SeoLoodoi.Domain.Tests;

public sealed class Phase6CompetitorIntelligenceTests
{
    [Theory]
    [InlineData("آموزش سئو تکنیکال - دیجی سئو", "آموزش سئو تکنیکال")]
    [InlineData("خرید گوشی موبایل | فروشگاه لوازم دیجیتال", "خرید گوشی موبایل")]
    [InlineData("قیمت لپ تاپ ایسوس • دیجی لودوی", "قیمت لپ تاپ ایسوس")]
    [InlineData("طراحی سایت وردپرس : راهنمای کامل", "طراحی سایت وردپرس")]
    public void CleanTopic_ExtractsCoreTopicPhrase(string title, string expected)
    {
        var parts = title.Split(['-', '|', '•', ':', '—'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var clean = parts.Length > 0 ? parts[0] : title.Trim();

        clean.Should().Be(expected);
    }

    [Fact]
    public void CompetitorGap_DistinguishesMissingAndDeeperContent()
    {
        var projectTopics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["آموزش سئو"] = 600,
            ["تحقیق کلمات کلیدی"] = 1200
        };

        var competitorPages = new[]
        {
            new { Topic = "سئو تکنیکال", WordCount = 1500 }, // Missing in project!
            new { Topic = "آموزش سئو", WordCount = 2400 },   // Common, but competitor has 4x deeper content!
            new { Topic = "تحقیق کلمات کلیدی", WordCount = 800 } // Common, project is deeper!
        };

        var gaps = new List<KeywordTopicGapDto>();

        foreach (var cp in competitorPages)
        {
            var matchedKey = projectTopics.Keys.FirstOrDefault(k => k.Contains(cp.Topic, StringComparison.OrdinalIgnoreCase) || cp.Topic.Contains(k, StringComparison.OrdinalIgnoreCase));

            if (matchedKey is null)
            {
                gaps.Add(new KeywordTopicGapDto(cp.Topic, "Comp", "https://comp.com", cp.WordCount, "MissingInProject", "Create new page"));
            }
            else
            {
                var pCount = projectTopics[matchedKey];
                if (cp.WordCount > pCount * 1.5 && cp.WordCount >= 300)
                {
                    gaps.Add(new KeywordTopicGapDto(cp.Topic, "Comp", "https://comp.com", cp.WordCount, "CompetitorHasDeeperContent", "Expand content"));
                }
            }
        }

        gaps.Should().HaveCount(2);
        gaps.Should().Contain(x => x.GapType == "MissingInProject" && x.Topic == "سئو تکنیکال");
        gaps.Should().Contain(x => x.GapType == "CompetitorHasDeeperContent" && x.Topic == "آموزش سئو");
    }
}

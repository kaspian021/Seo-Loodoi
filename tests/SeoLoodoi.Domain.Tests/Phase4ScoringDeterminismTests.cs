using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Scoring regression coverage: deterministic scores and unknown, not perfect, unmeasured categories.</summary>
public class Phase4ScoringDeterminismTests
{
    private static readonly IScoringEngine Engine = new ScoringEngine();
    private static SeoRuleResult Result(string code, bool triggered, IssueSeverity severity, IssueCategory category) =>
        new(code, triggered, severity, category, null);

    [Fact]
    public void Calculate_IsDeterministic()
    {
        var input = new[]
        {
            Result("TITLE_MISSING", true, IssueSeverity.High, IssueCategory.OnPage),
            Result("BROKEN_STATUS", true, IssueSeverity.High, IssueCategory.Technical),
            Result("THIN_CONTENT", false, IssueSeverity.Medium, IssueCategory.Content)
        };
        var first = Engine.Calculate(input);
        for (var i = 0; i < 5; i++)
        {
            var again = Engine.Calculate(input);
            Assert.Equal(first.Overall, again.Overall);
            Assert.Equal(first.Version, again.Version);
            Assert.Equal(first.IsPartial, again.IsPartial);
            Assert.Equal(first.Categories, again.Categories);
        }
    }

    [Fact]
    public void Calculate_LeavesUncoveredCategoriesNull()
    {
        var breakdown = Engine.Calculate([Result("TITLE_MISSING", false, IssueSeverity.High, IssueCategory.OnPage)]);
        Assert.Equal(100m, breakdown.Categories[IssueCategory.OnPage]);
        foreach (var category in Enum.GetValues<IssueCategory>().Where(c => c != IssueCategory.OnPage))
            Assert.Null(breakdown.Categories[category]);
        Assert.True(breakdown.IsPartial);
        Assert.Equal(new[] { IssueCategory.OnPage }, breakdown.CoveredCategories);
    }

    [Fact]
    public void Calculate_AppliesTheSeverityPenalty()
    {
        var score = Engine.Calculate([Result("X", true, IssueSeverity.High, IssueCategory.OnPage)]).Categories[IssueCategory.OnPage];
        Assert.Equal(75m, score);
    }

    [Theory]
    [InlineData(IssueSeverity.Critical, 60)]
    [InlineData(IssueSeverity.High, 75)]
    [InlineData(IssueSeverity.Medium, 85)]
    [InlineData(IssueSeverity.Low, 93)]
    [InlineData(IssueSeverity.Notice, 98)]
    public void Calculate_ScalesThePenaltyWithSeverity(IssueSeverity severity, decimal expected)
    {
        var score = Engine.Calculate([Result("X", true, severity, IssueCategory.OnPage)]).Categories[IssueCategory.OnPage];
        Assert.Equal(expected, score);
    }

    [Fact]
    public void Calculate_PenalisesInProportionToAffectedPages()
    {
        var results = new[]
        {
            Result("BROKEN_STATUS", true, IssueSeverity.High, IssueCategory.Technical),
            Result("BROKEN_STATUS", false, IssueSeverity.High, IssueCategory.Technical)
        };
        Assert.Equal(87.5m, Engine.Calculate(results).Categories[IssueCategory.Technical]);
    }

    [Fact]
    public void Calculate_CountsEachRuleCodeOnce()
    {
        var results = new[]
        {
            Result("A", true, IssueSeverity.Low, IssueCategory.OnPage),
            Result("B", true, IssueSeverity.Low, IssueCategory.OnPage)
        };
        Assert.Equal(86m, Engine.Calculate(results).Categories[IssueCategory.OnPage]);
    }

    [Fact]
    public void Calculate_NeverReturnsANegativeScore()
    {
        var results = Enum.GetValues<IssueSeverity>()
            .Select((s, i) => Result($"CODE_{i}", true, s, IssueCategory.OnPage)).ToArray();
        Assert.True(Engine.Calculate(results).Categories[IssueCategory.OnPage] >= 0m);
    }

    [Fact]
    public void Calculate_WeightsTheOverallScoreOverCoveredCategoriesOnly()
    {
        var results = new[]
        {
            Result("ONPAGE", false, IssueSeverity.Low, IssueCategory.OnPage),
            Result("TECH", true, IssueSeverity.High, IssueCategory.Technical)
        };
        var breakdown = Engine.Calculate(results);
        // (100 * .15 + 75 * .20) / (.15 + .20)
        Assert.Equal(85.7m, breakdown.Overall);
        Assert.Equal(2, breakdown.CoveredCategories.Count);
        Assert.True(breakdown.IsPartial);
    }

    [Fact]
    public void Calculate_ReturnsNullOverall_WhenNothingWasEvaluated()
    {
        var breakdown = Engine.Calculate([]);
        Assert.Null(breakdown.Overall);
        Assert.True(breakdown.IsPartial);
        Assert.Empty(breakdown.CoveredCategories);
    }

    [Fact]
    public void Calculate_ExposesAVersion()
    {
        Assert.Equal(ScoringEngine.Version, Engine.Calculate([]).Version);
        Assert.False(string.IsNullOrWhiteSpace(ScoringEngine.Version));
    }
}

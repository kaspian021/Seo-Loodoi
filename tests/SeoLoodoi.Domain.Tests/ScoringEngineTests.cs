using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public class ScoringEngineTests
{
    [Fact]
    public void Same_evidence_always_produces_same_score()
    {
        var evidence = new[] { new SeoRuleResult("X", true, IssueSeverity.High, IssueCategory.OnPage, null) };
        var sut = new ScoringEngine();
        var first = sut.Calculate(evidence);
        var second = sut.Calculate(evidence);
        second.Overall.Should().Be(first.Overall);
        second.Version.Should().Be(first.Version);
        second.Categories.Should().BeEquivalentTo(first.Categories);
    }
    [Fact]
    public void One_issue_changes_only_its_category()
    {
        var baseline = new ScoringEngine().Calculate([]);
        var changed = new ScoringEngine().Calculate([new("X", true, IssueSeverity.Medium, IssueCategory.Content, null)]);
        changed.Categories[IssueCategory.Content].Should().Be(85m);
        changed.Categories[IssueCategory.Technical].Should().Be(baseline.Categories[IssueCategory.Technical]);
    }
    [Fact]
    public void Penalty_is_proportional_to_affected_page_ratio()
    {
        var oneOfHundred = Enumerable.Range(0, 100).Select(i => new SeoRuleResult("TITLE_MISSING", i == 0, IssueSeverity.High, IssueCategory.OnPage, null)).ToArray();
        var ninetyOfHundred = Enumerable.Range(0, 100).Select(i => new SeoRuleResult("TITLE_MISSING", i < 90, IssueSeverity.High, IssueCategory.OnPage, null)).ToArray();
        var sut = new ScoringEngine();
        sut.Calculate(oneOfHundred).Categories[IssueCategory.OnPage].Should().Be(99.8m);
        sut.Calculate(ninetyOfHundred).Categories[IssueCategory.OnPage].Should().Be(77.5m);
    }
    [Fact]
    public void Categories_without_evidence_stay_null_instead_of_fabricated_100()
    {
        var result = new ScoringEngine().Calculate([new("TITLE_MISSING", true, IssueSeverity.High, IssueCategory.OnPage, null)]);
        result.Categories[IssueCategory.OnPage].Should().NotBeNull();
        result.Categories[IssueCategory.Technical].Should().BeNull();
        result.Categories[IssueCategory.Security].Should().BeNull();
        result.IsPartial.Should().BeTrue();
        result.CoveredCategories.Should().ContainSingle().Which.Should().Be(IssueCategory.OnPage);
    }
    [Fact]
    public void Overall_uses_only_covered_categories()
    {
        var onPageOnly = new ScoringEngine().Calculate([new("X", true, IssueSeverity.Medium, IssueCategory.OnPage, null)]);
        onPageOnly.Overall.Should().Be(onPageOnly.Categories[IssueCategory.OnPage]);
        var empty = new ScoringEngine().Calculate([]);
        empty.Overall.Should().BeNull();
        empty.IsPartial.Should().BeTrue();
        empty.CoveredCategories.Should().BeEmpty();
    }
    [Fact]
    public void Fully_covered_snapshot_is_not_partial()
    {
        var results = Enum.GetValues<IssueCategory>().Select(c => new SeoRuleResult("X", false, IssueSeverity.Low, c, null)).ToArray();
        var result = new ScoringEngine().Calculate(results);
        result.IsPartial.Should().BeFalse();
        result.Overall.Should().Be(100m);
        result.Categories.Values.Should().AllSatisfy(x => x.Should().Be(100m));
    }
    [Fact]
    public void Scoring_version_is_2_for_nullable_evidence()
    {
        new ScoringEngine().Calculate([]).Version.Should().Be("2.0.0");
        ScoringEngine.Version.Should().Be("2.0.0");
    }
}

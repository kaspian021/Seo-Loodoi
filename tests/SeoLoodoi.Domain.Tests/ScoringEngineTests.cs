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
        changed.Categories[IssueCategory.Content].Should().BeLessThan(baseline.Categories[IssueCategory.Content]);
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
}

using AwesomeAssertions;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public class AnalysisLifecycleTests
{
    [Fact]
    public void Crawl_analysis_moves_through_pending_running_succeeded()
    {
        var analysis = new CrawlAnalysis(Guid.NewGuid(), Guid.NewGuid(), "analyze-crawl:1");
        analysis.Status.Should().Be(AnalysisStatus.Pending);
        analysis.MarkRunning(DateTimeOffset.UtcNow, "analyze-crawl:1");
        analysis.Status.Should().Be(AnalysisStatus.Running);
        analysis.Attempts.Should().Be(1);
        analysis.MarkSucceeded(DateTimeOffset.UtcNow);
        analysis.Status.Should().Be(AnalysisStatus.Succeeded);
        analysis.FinishedAt.Should().NotBeNull();
    }
    [Fact]
    public void Failed_analysis_can_be_reset_for_retry_with_new_key()
    {
        var analysis = new CrawlAnalysis(Guid.NewGuid(), Guid.NewGuid(), "analyze-crawl:1");
        analysis.MarkRunning(DateTimeOffset.UtcNow, "analyze-crawl:1");
        analysis.MarkFailed("boom", DateTimeOffset.UtcNow);
        analysis.ResetForRetry("analyze-crawl:1:retry:2", DateTimeOffset.UtcNow);
        analysis.Status.Should().Be(AnalysisStatus.Pending);
        analysis.LastJobKey.Should().Be("analyze-crawl:1:retry:2");
        analysis.LastError.Should().BeNull();
    }
    [Fact]
    public void Running_or_succeeded_analysis_cannot_be_reset()
    {
        var running = new CrawlAnalysis(Guid.NewGuid(), Guid.NewGuid(), "k");
        running.MarkRunning(DateTimeOffset.UtcNow, "k");
        var retryRunning = () => running.ResetForRetry("k2", DateTimeOffset.UtcNow);
        retryRunning.Should().Throw<InvalidOperationException>();
        var succeeded = new CrawlAnalysis(Guid.NewGuid(), Guid.NewGuid(), "k");
        succeeded.MarkSucceeded(DateTimeOffset.UtcNow);
        var retrySucceeded = () => succeeded.ResetForRetry("k2", DateTimeOffset.UtcNow);
        retrySucceeded.Should().Throw<InvalidOperationException>();
    }
    [Fact]
    public void Score_snapshot_preserves_null_categories_and_partial_flag()
    {
        var breakdown = new ScoringEngine().Calculate([new SeoRuleResult("X", true, IssueSeverity.High, IssueCategory.OnPage, null)]);
        var snapshot = new SeoScoreSnapshot(Guid.NewGuid(), Guid.NewGuid(), breakdown.Overall, breakdown.Categories, breakdown.Version, breakdown.IsPartial);
        snapshot.OnPageScore.Should().NotBeNull();
        snapshot.TechnicalScore.Should().BeNull();
        snapshot.OverallScore.Should().Be(breakdown.Overall);
        snapshot.IsPartial.Should().BeTrue();
        snapshot.CalculationVersion.Should().Be("2.0.0");
    }
    [Fact]
    public void Competitor_crawl_is_bounded_and_lifecycle_guarded()
    {
        CompetitorCrawl.MaxPages.Should().Be(25);
        CompetitorCrawl.MaxDepth.Should().Be(1);
        var crawl = new CompetitorCrawl(Guid.NewGuid(), Guid.NewGuid());
        crawl.Status.Should().Be(CompetitorCrawlStatus.Queued);
        crawl.Start(DateTimeOffset.UtcNow);
        var restart = () => crawl.Start(DateTimeOffset.UtcNow);
        restart.Should().Throw<InvalidOperationException>();
        crawl.Complete("{}", DateTimeOffset.UtcNow);
        crawl.Status.Should().Be(CompetitorCrawlStatus.Completed);
    }
    [Fact]
    public void Competitor_crawl_failure_is_bounded_and_cancel_safe()
    {
        var crawl = new CompetitorCrawl(Guid.NewGuid(), Guid.NewGuid());
        crawl.Fail(new string('x', 5000), DateTimeOffset.UtcNow);
        crawl.Status.Should().Be(CompetitorCrawlStatus.Failed);
        crawl.LastError.Should().HaveLength(2000);
        crawl.Cancel(DateTimeOffset.UtcNow);
        crawl.Status.Should().Be(CompetitorCrawlStatus.Cancelled);
    }
}

using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Jobs;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F8 regression: when the monthly page quota is exhausted, the scheduler must
/// defer the crawl until the quota resets (start of next month) instead of
/// retrying every few minutes and logging churn until the period rolls over.
/// </summary>
public sealed class ScheduledCrawlQuotaBackoffTests
{
    [Theory]
    [InlineData(2026, 9, 13, 12, 0, 2026, 10, 1)] // mid-month
    [InlineData(2026, 9, 30, 23, 59, 2026, 10, 1)] // last instant of the month
    [InlineData(2026, 12, 15, 8, 0, 2027, 1, 1)]  // year rollover
    public void NextQuotaReset_IsFirstOfNextMonthUtc(int year, int month, int day, int hour, int minute, int expectedYear, int expectedMonth, int expectedDay)
    {
        var now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

        var reset = ScheduledCrawlWorker.NextQuotaReset(now);

        reset.Should().Be(new DateTimeOffset(expectedYear, expectedMonth, expectedDay, 0, 0, 0, TimeSpan.Zero),
            "the monthly quota resets at the start of the next calendar month (UTC)");
    }

    [Fact]
    public void DeferCrawlUntil_SetsNextCrawlWithoutRecordingACrawl()
    {
        var project = new SeoProject(Guid.NewGuid(), "Backoff", new Uri("https://backoff.example"));
        var deferral = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        project.DeferCrawlUntil(deferral);

        project.NextCrawlAt.Should().Be(deferral, "the scheduler must not retry until the quota resets");
        project.LastCrawlAt.Should().BeNull("no crawl actually ran; the deferral must not fabricate one");
    }

    [Fact]
    public void QuotaExceededException_IsDistinctFromGenericInvalidOperation()
    {
        // The worker relies on catching the quota subclass separately from the
        // "a crawl is already running" path, which keeps the short retry.
        new QuotaExceededException("quota").Should().BeAssignableTo<InvalidOperationException>();
    }
}

using SeoLoodoi.Domain.Seo;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

public sealed class CrawlLifecycleRegressionTests
{
    private static Crawl RunningCrawl()
    {
        var crawl = new Crawl(Guid.NewGuid(), CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow);
        return crawl;
    }

    [Fact]
    public void Cancel_AfterFailure_KeepsTheFailureState()
    {
        // The failure reason and terminal state are audit evidence; a later
        // cancel must not silently rewrite a failed crawl into Cancelled.
        var crawl = RunningCrawl();
        crawl.Fail("worker crashed", DateTimeOffset.UtcNow);

        crawl.Cancel(DateTimeOffset.UtcNow.AddMinutes(1));

        crawl.Status.Should().Be(CrawlStatus.Failed);
        crawl.ErrorMessage.Should().Be("worker crashed");
    }

    [Fact]
    public void Cancel_RunningCrawl_Cancels()
    {
        var crawl = RunningCrawl();

        crawl.Cancel(DateTimeOffset.UtcNow);

        crawl.Status.Should().Be(CrawlStatus.Cancelled);
    }

    [Fact]
    public void Cancel_CompletedCrawl_IsIgnored()
    {
        var crawl = RunningCrawl();
        crawl.Complete(DateTimeOffset.UtcNow);

        crawl.Cancel(DateTimeOffset.UtcNow.AddMinutes(1));

        crawl.Status.Should().Be(CrawlStatus.Completed);
    }
}

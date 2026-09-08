using System.Reflection;
using AwesomeAssertions;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Domain.Tests;

public class DurableStateTests
{
    [Fact]
    public void Crawl_enforces_state_transitions()
    {
        var now = DateTimeOffset.UtcNow; var crawl = new Crawl(Guid.NewGuid(), CrawlTrigger.Manual);
        crawl.Start(now); crawl.ReportDiscovered(3); crawl.ReportCrawled(); crawl.Pause(now.AddSeconds(1)); crawl.Start(now.AddSeconds(2)); crawl.Complete(now.AddSeconds(3));
        crawl.Status.Should().Be(CrawlStatus.Completed); crawl.PagesDiscovered.Should().Be(3); crawl.PagesCrawled.Should().Be(1);
        var act = () => crawl.Start(now.AddSeconds(4)); act.Should().Throw<InvalidOperationException>();
    }
    [Fact]
    public void Expired_frontier_lease_can_be_reclaimed()
    {
        var now = DateTimeOffset.UtcNow; var item = new CrawlFrontierItem(Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "https://example.com/", 0);
        item.TryLease("worker-a", now, TimeSpan.FromSeconds(10)).Should().BeTrue();
        item.TryLease("worker-b", now.AddSeconds(11), TimeSpan.FromSeconds(10)).Should().BeTrue();
        item.LeaseOwner.Should().Be("worker-b"); item.Attempts.Should().Be(2);
    }
    [Fact]
    public void Frontier_retry_eventually_becomes_terminal()
    {
        var now = DateTimeOffset.UtcNow; var item = new CrawlFrontierItem(Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "https://example.com/", 0);
        item.TryLease("w", now, TimeSpan.FromMinutes(1)); item.Retry(now, TimeSpan.Zero, "first", 2);
        item.Status.Should().Be(FrontierStatus.Pending);
        item.TryLease("w", now, TimeSpan.FromMinutes(1)); item.Retry(now, TimeSpan.Zero, "second", 2);
        item.Status.Should().Be(FrontierStatus.Failed);
    }
    [Fact]
    public void Frontier_lease_with_missing_expiry_can_be_reclaimed()
    {
        // Legacy rows may carry Status=Leased with a null LeaseExpiresAt. They
        // must be treated as expired instead of wedging the crawl forever.
        var now = DateTimeOffset.UtcNow; var item = new CrawlFrontierItem(Guid.NewGuid(), Guid.NewGuid(), "https://example.com", "https://example.com/", 0);
        item.TryLease("worker-a", now, TimeSpan.FromMinutes(2)).Should().BeTrue();
        typeof(CrawlFrontierItem).GetProperty("LeaseExpiresAt")!.GetSetMethod(nonPublic: true)!.Invoke(item, [null]);
        item.TryLease("worker-b", now.AddMinutes(3), TimeSpan.FromMinutes(2)).Should().BeTrue();
        item.LeaseOwner.Should().Be("worker-b");
    }
    [Fact]
    public void Job_lease_with_missing_expiry_can_be_reclaimed()
    {
        var now = DateTimeOffset.UtcNow; var job = new SeoBackgroundJob(SeoJobType.ContinueCrawl, "crawl:stuck");
        job.TryLease("worker-a", now, TimeSpan.FromMinutes(2)).Should().BeTrue();
        typeof(SeoBackgroundJob).GetProperty("LeaseExpiresAt")!.GetSetMethod(nonPublic: true)!.Invoke(job, [null]);
        job.TryLease("worker-b", now.AddMinutes(3), TimeSpan.FromMinutes(2)).Should().BeTrue();
        job.LeaseOwner.Should().Be("worker-b");
    }
    [Fact]
    public void Durable_job_retries_with_backoff_and_terminal_limit()
    {
        var now = DateTimeOffset.UtcNow; var job = new SeoBackgroundJob(SeoJobType.InitialCrawl, "crawl:one");
        job.TryLease("w", now, TimeSpan.FromMinutes(1)).Should().BeTrue(); job.Retry(now, TimeSpan.FromSeconds(10), "network", 2);
        job.TryLease("w", now.AddSeconds(5), TimeSpan.FromMinutes(1)).Should().BeFalse();
        job.TryLease("w", now.AddSeconds(11), TimeSpan.FromMinutes(1)).Should().BeTrue(); job.Retry(now.AddSeconds(11), TimeSpan.Zero, "network", 2);
        job.Status.Should().Be(SeoJobStatus.Failed);
    }
}

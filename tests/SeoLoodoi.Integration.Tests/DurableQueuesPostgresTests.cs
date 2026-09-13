using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// Executes the Postgres-only raw-SQL queue paths (ON CONFLICT dedupe and
/// FOR UPDATE SKIP LOCKED leasing) against a real PostgreSQL 16 container.
/// The InMemory provider never runs these statements.
/// </summary>
[Collection("postgres")]
public sealed class DurableQueuesPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Frontier_DuplicateNormalizedUrl_IsRejectedByOnConflict()
    {
        await using var db = fixture.CreateContext();
        var store = new CrawlFrontierStore(db);
        var crawlId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var first = await store.EnqueueAsync(new CrawlFrontierItem(crawlId, projectId, "https://example.com/a", "https://example.com/a", 0), CancellationToken.None);
        var second = await store.EnqueueAsync(new CrawlFrontierItem(crawlId, projectId, "https://example.com/a?utm_x=1", "https://example.com/a", 1), CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse();
        var rows = await db.CrawlFrontierItems.CountAsync(x => x.CrawlId == crawlId);
        rows.Should().Be(1);
    }

    [Fact]
    public async Task Frontier_Lease_IsExclusive_UntilItExpires()
    {
        await using var db1 = fixture.CreateContext();
        await using var db2 = fixture.CreateContext();
        var store1 = new CrawlFrontierStore(db1);
        var store2 = new CrawlFrontierStore(db2);
        var crawlId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store1.EnqueueAsync(new CrawlFrontierItem(crawlId, projectId, "https://example.com/b", "https://example.com/b", 0), CancellationToken.None);

        var leased = await store1.TryLeaseAsync(crawlId, "worker-1", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        var concurrent = await store2.TryLeaseAsync(crawlId, "worker-2", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        var afterExpiry = await store2.TryLeaseAsync(crawlId, "worker-2", now.AddMinutes(3), TimeSpan.FromMinutes(2), CancellationToken.None);

        leased.Should().NotBeNull();
        leased!.LeaseOwner.Should().Be("worker-1");
        concurrent.Should().BeNull("SKIP LOCKED must hide the live foreign lease");
        afterExpiry.Should().NotBeNull("an expired lease must become leasable again");
        afterExpiry!.LeaseOwner.Should().Be("worker-2");
        afterExpiry.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task JobQueue_EnqueueOnce_IsIdempotent_OnRealPostgres()
    {
        await using var db = fixture.CreateContext();
        var queue = new SeoJobQueue(db);
        var key = $"itest-once:{Guid.NewGuid():N}";

        var first = await queue.EnqueueOnceAsync(SeoJobType.ContinueCrawl, key, "{}", CancellationToken.None);
        var second = await queue.EnqueueOnceAsync(SeoJobType.ContinueCrawl, key, "{\"ignored\":true}", CancellationToken.None);

        second.Should().Be(first);
        var jobs = await db.SeoBackgroundJobs.CountAsync(x => x.IdempotencyKey == key);
        jobs.Should().Be(1);
        var payload = await db.SeoBackgroundJobs.Where(x => x.IdempotencyKey == key).Select(x => x.PayloadJson).SingleAsync();
        payload.Should().Be("{}", "the first writer's payload must never be rewritten");
    }

    [Fact]
    public async Task JobQueue_NotBefore_DelaysLeasing()
    {
        await using var db = fixture.CreateContext();
        var queue = new SeoJobQueue(db);
        var key = $"itest-notbefore:{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await queue.EnqueueOnceAsync(SeoJobType.ContinueCrawl, key, "{}", CancellationToken.None, notBefore: now.AddHours(1));

        var tooEarly = await queue.TryLeaseAsync("worker", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        var dueTime = await queue.TryLeaseAsync("worker", now.AddHours(2), TimeSpan.FromMinutes(2), CancellationToken.None);

        tooEarly.Should().BeNull();
        dueTime.Should().NotBeNull();
        dueTime!.IdempotencyKey.Should().Be(key);
    }

    [Fact]
    public async Task JobQueue_Retry_Backoff_SetsNotBeforeAndKeepsHistory()
    {
        await using var db = fixture.CreateContext();
        var queue = new SeoJobQueue(db);
        var key = $"itest-retry:{Guid.NewGuid():N}";
        await queue.EnqueueOnceAsync(SeoJobType.AnalyzeCrawl, key, "{}", CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var job = await queue.TryLeaseAsync("worker", now, TimeSpan.FromMinutes(2), CancellationToken.None);
        job.Should().NotBeNull();

        job!.Retry(now, TimeSpan.FromSeconds(40), "boom", 5);
        await queue.SaveAsync(CancellationToken.None);

        var reloaded = await db.SeoBackgroundJobs.SingleAsync(x => x.Id == job.Id);
        reloaded.Status.Should().Be(SeoJobStatus.Queued);
        reloaded.NotBefore.Should().BeCloseTo(now.AddSeconds(40), TimeSpan.FromSeconds(1));
        reloaded.LastError.Should().Be("boom");
    }
}

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Application.Urls;

namespace SeoLoodoi.Domain.Tests.Postgres;

/// <summary>
/// F-03 regression on a REAL PostgreSQL server: the batch runner holds the
/// crawl entity across a pause command. With the ConcurrencyStamp token
/// (migration 20260909130000) the runner's stale save is rejected with a
/// concurrency exception instead of resurrecting the paused crawl.
/// InMemory cannot prove this (it does not implement concurrency tokens),
/// so only a live database can.
/// </summary>
[Collection("postgres")]
public class CrawlConcurrencyTokenPostgresTests(PostgresFixture fixture)
{
    private static CrawlCommandService CommandServiceFor(AppDbContext db) =>
        new(db, new CrawlFrontierStore(db), new SeoJobQueue(db), new UrlNormalizer(),
            new AccessStub(), new QuotaStub(), new AuditStub());

    [Fact]
    public async Task Stale_save_after_pause_is_rejected_and_the_pause_survives()
    {
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
        var owner = Guid.NewGuid();
        using var seed = fixture.CreateContext();
        var host = "crawl-lock-" + Guid.NewGuid().ToString("N")[..12];
        var project = new SeoProject(owner, "Lock Project", new Uri($"https://{host}.example.com"));
        seed.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow);
        seed.Crawls.Add(crawl);
        await seed.SaveChangesAsync();
        string tokenBefore = crawl.ConcurrencyStamp;
        try
        {
            // "The runner": loads and modifies the crawl (tracked entity).
            using var runner = fixture.CreateContext();
            var tracked = await runner.Crawls.SingleAsync(x => x.Id == crawl.Id);
            // A clearly distinct heartbeat: if the stale save were applied it
            // would be visible here.
            tracked.Heartbeat(DateTimeOffset.UtcNow.AddMinutes(5));

            // A pause command commits while the runner holds its stale copy.
            using var commander = fixture.CreateContext();
            var paused = await CommandServiceFor(commander).PauseAsync(project.Id, crawl.Id, owner, CancellationToken.None);
            paused.Should().BeTrue();
            using var after = fixture.CreateContext();
            string tokenAfter = (await after.Crawls.AsNoTracking().SingleAsync(x => x.Id == crawl.Id)).ConcurrencyStamp;
            tokenAfter.Should().NotBe(tokenBefore, "the pause must advance the concurrency token");

            // The runner's stale save must now be rejected.
            DbUpdateException? caught = null;
            try { await runner.SaveChangesAsync(); }
            catch (DbUpdateException ex) { caught = ex; }
            caught.Should().BeOfType<DbUpdateConcurrencyException>("stale token -> concurrency exception, not a silent overwrite");

            // The pause survives; the runner's heartbeat was not applied.
            using var check = fixture.CreateContext();
            var final = await check.Crawls.AsNoTracking().SingleAsync(x => x.Id == crawl.Id);
            final.Status.Should().Be(CrawlStatus.Paused);
            final.HeartbeatAt.Should().BeNull("the rejected stale save must not have written the heartbeat");
        }
        finally
        {
            await seed.CrawledUrls.Where(x => x.ProjectId == project.Id).ExecuteDeleteAsync();
            await seed.CrawlFrontierItems.Where(x => x.CrawlId == crawl.Id).ExecuteDeleteAsync();
            await seed.SeoBackgroundJobs.Where(x => x.IdempotencyKey == $"initial-crawl:{crawl.Id}").ExecuteDeleteAsync();
            await seed.Crawls.Where(x => x.Id == crawl.Id).ExecuteDeleteAsync();
            await seed.SeoProjects.Where(x => x.Id == project.Id).ExecuteDeleteAsync();
        }
    }
}

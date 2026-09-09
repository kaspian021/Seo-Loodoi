using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Application.Urls;

namespace SeoLoodoi.Domain.Tests.Postgres;

/// <summary>
/// F-04 regression on a REAL PostgreSQL server: two concurrent POST /crawls
/// (StartAsync) previously aborted one caller with an unhandled 40001
/// serialization failure (HTTP 500). After the fix, exactly one start wins
/// and the loser takes the normal "active crawl already exists" 409 path.
/// Reproduced pre-fix on a live PostgreSQL server (phase1-logs/race-repro-f01-f04.log).
/// </summary>
[Collection("postgres")]
public class CrawlCommandServicePostgresTests(PostgresFixture fixture)
{
    private sealed record Outcome(Crawl? Crawl, string? ExceptionType, string? Message);

    private async Task<Outcome> TryStartAsync(Guid project, Guid owner)
    {
        using var db = fixture.CreateContext();
        var service = new CrawlCommandService(
            db, new CrawlFrontierStore(db), new SeoJobQueue(db), new UrlNormalizer(),
            new AccessStub(), new QuotaStub(), new AuditStub());
        try
        {
            var crawl = await service.StartAsync(project, owner, CancellationToken.None);
            return new Outcome(crawl, null, null);
        }
        catch (Exception ex)
        {
            return new Outcome(null, ex.GetType().Name, ex.Message);
        }
    }

    [Fact]
    public async Task Concurrent_crawl_starts_exactly_one_wins_and_the_loser_gets_the_409_path()
    {
        Assert.SkipIf(string.IsNullOrWhiteSpace(fixture.ConnectionString), PostgresFixture.SkipReason);
        var owner = Guid.NewGuid();
        using var seed = fixture.CreateContext();
        var host = "crawl-race-" + Guid.NewGuid().ToString("N")[..12];
        var project = new SeoProject(owner, "Race Project", new Uri($"https://{host}.example.com"));
        seed.SeoProjects.Add(project);
        await seed.SaveChangesAsync();
        try
        {
            var outcomes = (await Task.WhenAll(TryStartAsync(project.Id, owner), TryStartAsync(project.Id, owner))).ToArray();
            var winner = outcomes.Single(o => o.Crawl is not null).Crawl!;
            var loser = outcomes.Single(o => o.Crawl is null);

            loser.ExceptionType.Should().Be("InvalidOperationException",
                "the loser must take the normal 409 'active crawl' path, never a raw 40001/DbUpdateException");
            loser.Message.Should().Be("An active crawl already exists.");

            using var check = fixture.CreateContext();
            (await check.Crawls.CountAsync(x => x.ProjectId == project.Id)).Should().Be(1, "exactly one crawl row exists");
            (await check.CrawlFrontierItems.CountAsync(x => x.CrawlId == winner.Id)).Should().Be(1, "the winning crawl enqueued its frontier item");
            (await check.SeoBackgroundJobs.CountAsync(x => x.IdempotencyKey == $"initial-crawl:{winner.Id}")).Should().Be(1, "the winning crawl enqueued its initial job");
        }
        finally
        {
            var crawlIds = (await seed.Crawls.Where(x => x.ProjectId == project.Id).Select(x => x.Id).ToListAsync()).ToArray();
            if (crawlIds.Length > 0)
            {
                await seed.CrawlFrontierItems.Where(x => crawlIds.Contains(x.CrawlId)).ExecuteDeleteAsync();
                foreach (var id in crawlIds)
                    await seed.SeoBackgroundJobs.Where(x => x.IdempotencyKey == $"initial-crawl:{id}").ExecuteDeleteAsync();
                await seed.Crawls.Where(x => x.ProjectId == project.Id).ExecuteDeleteAsync();
            }
            await seed.SeoProjects.Where(x => x.Id == project.Id).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Second_sequential_start_while_first_is_active_gets_the_409_path()
    {
        Assert.SkipIf(string.IsNullOrWhiteSpace(fixture.ConnectionString), PostgresFixture.SkipReason);
        var owner = Guid.NewGuid();
        using var seed = fixture.CreateContext();
        var host = "crawl-seq-" + Guid.NewGuid().ToString("N")[..12];
        var project = new SeoProject(owner, "Sequential Project", new Uri($"https://{host}.example.com"));
        seed.SeoProjects.Add(project);
        await seed.SaveChangesAsync();
        try
        {
            var first = await TryStartAsync(project.Id, owner);
            first.Crawl.Should().NotBeNull();
            var second = await TryStartAsync(project.Id, owner);
            second.Crawl.Should().BeNull();
            second.ExceptionType.Should().Be("InvalidOperationException");
            (await seed.Crawls.CountAsync(x => x.ProjectId == project.Id)).Should().Be(1);
        }
        finally
        {
            var crawlIds = (await seed.Crawls.Where(x => x.ProjectId == project.Id).Select(x => x.Id).ToListAsync()).ToArray();
            if (crawlIds.Length > 0)
            {
                await seed.CrawlFrontierItems.Where(x => crawlIds.Contains(x.CrawlId)).ExecuteDeleteAsync();
                foreach (var id in crawlIds)
                    await seed.SeoBackgroundJobs.Where(x => x.IdempotencyKey == $"initial-crawl:{id}").ExecuteDeleteAsync();
                await seed.Crawls.Where(x => x.ProjectId == project.Id).ExecuteDeleteAsync();
            }
            await seed.SeoProjects.Where(x => x.Id == project.Id).ExecuteDeleteAsync();
        }
    }
}

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

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
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
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

    /// <summary>
    /// F-05 regression on real PG: the quota check must be inside the
    /// serializable transaction. A concurrent transaction that fills the
    /// monthly page budget between the check and the commit must result in
    /// either the crawl being rejected (429) or the page rows being rolled
    /// back — never a new crawl coexisting with a full budget (which is what
    /// the pre-fix check-outside-transaction allowed).
    /// </summary>
    [Fact]
    public async Task New_crawl_never_coexists_with_a_full_monthly_budget()
    {
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
        var owner = Guid.NewGuid();
        var pagesPerMonth = 5;
        var options = new QuotaOptions { DefaultPlan = "Starter", MaxProjects = 3, PagesPerMonth = pagesPerMonth, MaxKeywords = 25, MaxCompetitors = 3 };
        using var seed = fixture.CreateContext();
        var host = "quota-race-" + Guid.NewGuid().ToString("N")[..12];
        var project = new SeoProject(owner, "Quota Race", new Uri($"https://{host}.example.com"));
        seed.SeoProjects.Add(project);
        // Seed 3 used pages from a completed crawl started this month.
        var seedCrawl = new Crawl(project.Id, CrawlTrigger.Manual);
        seedCrawl.Start(DateTimeOffset.UtcNow.AddDays(-1));
        seedCrawl.Complete(DateTimeOffset.UtcNow);
        seed.Crawls.Add(seedCrawl);
        for (var i = 0; i < 3; i++)
            seed.CrawledUrls.Add(new CrawledUrl(seedCrawl.Id, project.Id, $"https://{host}.example.com/s{i}", null, 200, "text/html", 0, 50, true, 50, null));
        await seed.SaveChangesAsync();
        try
        {
            // B: two page rows that fill the budget once committed (3 + 2 = 5).
            using var b = fixture.CreateContext();
            var bTx = await b.Database.BeginTransactionAsync();
            for (var i = 0; i < 2; i++)
                b.CrawledUrls.Add(new CrawledUrl(seedCrawl.Id, project.Id, $"https://{host}.example.com/b{i}", null, 200, "text/html", 0, 50, true, 50, null));
            await b.SaveChangesAsync(); // inserted but UNCOMMITTED

            // A: starts the crawl while B is uncommitted (at check time: 3 + 2 <= 5).
            using var a = fixture.CreateContext();
            var service = new CrawlCommandService(
                a, new CrawlFrontierStore(a), new SeoJobQueue(a), new UrlNormalizer(),
                new AccessStub(), new QuotaService(a, Options.Create(options)), new AuditStub());
            Crawl? aCrawl = null;
            Exception? aError = null;
            try { aCrawl = await service.StartAsync(project.Id, owner, CancellationToken.None); }
            catch (Exception ex) { aError = ex; }

            // B now commits. Under SSI exactly one of {A's crawl, B's pages} survives.
            try { await bTx.CommitAsync(); }
            catch (Exception) { await bTx.RollbackAsync(); }

            var periodStart = new DateTimeOffset(new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            using var check = fixture.CreateContext();
            var used = await check.CrawledUrls.CountAsync(x => x.ProjectId == project.Id && check.Crawls.Any(c => c.Id == x.CrawlId && c.StartedAt >= periodStart));
            var newCrawls = await check.Crawls.CountAsync(x => x.ProjectId == project.Id && x.Id != seedCrawl.Id);

            if (aCrawl is not null)
            {
                aError.Should().BeNull();
                used.Should().Be(3, "A won the race, so B's budget-filling pages must have been rolled back by SSI");
            }
            else
            {
                aError.Should().BeOfType<QuotaExceededException>("the re-check after the SSI abort sees the full budget and must 429");
                newCrawls.Should().Be(0);
                used.Should().Be(pagesPerMonth);
            }
            (newCrawls > 0 && used >= pagesPerMonth).Should().BeFalse(
                "F-05 invariant: a new crawl must never be allowed once the monthly budget is full");
        }
        finally
        {
            var crawlIds = (await seed.Crawls.Where(x => x.ProjectId == project.Id).Select(x => x.Id).ToListAsync()).ToArray();
            await seed.CrawledUrls.Where(x => x.ProjectId == project.Id).ExecuteDeleteAsync();
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
        fixture.ReportUnavailable();
        if (!fixture.DatabaseAvailable) return;
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

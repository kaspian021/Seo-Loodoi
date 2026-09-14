using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Analysis;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using SeoLoodoi.Infrastructure.Security;
using AwesomeAssertions;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// End-to-end engine execution on real PostgreSQL: start → batch crawl of a
/// live public site (example.com) → completion under the row lock → analysis
/// pipeline rebuilding issues and the score snapshot. Every raw-SQL path that
/// the InMemory provider skips is exercised here.
/// </summary>
[Collection("postgres")]
public sealed class CrawlPipelinePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StartCrawl_BatchRun_Analysis_SucceedOnRealPostgres()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;
        await using var db = fixture.CreateContext();
        var normalizer = new UrlNormalizer();
        var store = new CrawlFrontierStore(db);
        var queue = new SeoJobQueue(db);
        var access = new ProjectAccessService(db);
        var quota = new QuotaService(db, Options.Create(new QuotaOptions()));
        var audit = new AuditLogService(db, access);
        var commands = new CrawlCommandService(db, store, queue, normalizer, access, quota, audit);

        var ownerId = Guid.NewGuid();
        var project = new SeoProject(ownerId, "Integration pipeline", new Uri("https://example.com"));
        db.SeoProjects.Add(project);
        await db.SaveChangesAsync(ct);

        // 1) Start: quota reservation, serializable transaction, durable seeds.
        var crawl = await commands.StartAsync(project.Id, ownerId, ct);
        crawl.Should().NotBeNull();
        crawl!.Status.Should().Be(CrawlStatus.Queued);
        (await db.CrawlFrontierItems.CountAsync(x => x.CrawlId == crawl.Id, ct)).Should().Be(1);
        (await db.SeoBackgroundJobs.AnyAsync(x => x.IdempotencyKey == $"initial-crawl:{crawl.Id}", ct)).Should().BeTrue();

        // A second concurrent start must be rejected while the first is active.
        var duplicate = () => commands.StartAsync(project.Id, ownerId, ct);
        await duplicate.Should().ThrowAsync<InvalidOperationException>();

        // 2) Batch crawl against the live site (example.com: 1 page, no internal links).
        var guard = new OutboundUrlGuard();
        var fetcher = new SafePageFetcher(new HttpClient(SsrfPinnedHandler.Create(h => h.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate)), guard, new HostRequestCoordinator(TimeProvider.System));
        var robots = new RobotsService(fetcher, new RobotsParser(), new MemoryCache(new MemoryCacheOptions()));
        var sitemaps = new SitemapDiscoveryService(fetcher, new SitemapParser());
        var planner = new CrawlFrontierPlanner(normalizer, store);
        var runner = new CrawlBatchRunner(db, store, queue, fetcher, new HtmlExtractor(), robots, sitemaps, planner, normalizer, NullLogger<CrawlBatchRunner>.Instance);

        var crawlJob = await db.SeoBackgroundJobs.SingleAsync(x => x.IdempotencyKey == $"initial-crawl:{crawl.Id}", ct);
        await runner.RunAsync(crawlJob, ct);

        await db.Entry(crawl).ReloadAsync();
        crawl.Status.Should().Be(CrawlStatus.Completed);
        crawl.PagesCrawled.Should().BeGreaterThanOrEqualTo(1);
        (await db.CrawledUrls.CountAsync(x => x.CrawlId == crawl.Id, ct)).Should().BeGreaterThanOrEqualTo(1);
        (await db.PageSnapshots.CountAsync(x => x.CrawlId == crawl.Id, ct)).Should().BeGreaterThanOrEqualTo(1);
        (await db.CrawlAnalyses.SingleAsync(x => x.CrawlId == crawl.Id, ct)).Status.Should().Be(AnalysisStatus.Pending);
        (await db.SeoBackgroundJobs.AnyAsync(x => x.IdempotencyKey == $"analyze-crawl:{crawl.Id}", ct)).Should().BeTrue();

        // 3) Analysis pipeline: deterministic rules → issues → score snapshot.
        var rules = new ISeoRule[]
        {
            new TitleMissingRule(), new TitleLengthRule(), new MetaDescriptionMissingRule(), new H1MissingRule(),
            new MultipleH1Rule(), new CanonicalMissingRule(), new LowWordCountRule(), new MissingAltRule(),
            new SlowResponseRule(), new MetaDescriptionLengthRule(), new CanonicalInvalidRule(), new CanonicalMismatchRule(),
            new NoIndexRule(), new HttpsIssueRule(), new HeadingStructureRule(), new BrokenStatusRule(),
            new RedirectedStatusRule(), new XRobotsNoIndexRule(), new EmptyContentTypeRule(),
        };
        var analyzer = new AnalyzeCrawlJobHandler(db, rules, new ScoringEngine(), new ContentSimilarityEngine(), new InternalLinkGraph(), normalizer, NullLogger<AnalyzeCrawlJobHandler>.Instance);
        var analysisJob = await db.SeoBackgroundJobs.SingleAsync(x => x.IdempotencyKey == $"analyze-crawl:{crawl.Id}", ct);
        await analyzer.HandleAsync(analysisJob, ct);

        var analysis = await db.CrawlAnalyses.SingleAsync(x => x.CrawlId == crawl.Id, ct);
        analysis.Status.Should().Be(AnalysisStatus.Succeeded);
        var score = await db.SeoScores.SingleAsync(x => x.CrawlId == crawl.Id, ct);
        score.CalculationVersion.Should().Be(ScoringEngine.Version);
        score.OverallScore.Should().NotBeNull();
        (await db.SeoIssues.CountAsync(x => x.CrawlId == crawl.Id, ct)).Should().BeGreaterThan(0, "example.com has no canonical and little text");

        // 4) Re-running analysis is idempotent: evidence is rebuilt, not duplicated.
        var analysisRowBefore = analysis;
        await analyzer.HandleAsync(analysisJob, ct);
        (await db.SeoScores.CountAsync(x => x.CrawlId == crawl.Id, ct)).Should().Be(1);
        analysisRowBefore.Status.Should().Be(AnalysisStatus.Succeeded);
    }

    [Fact]
    public async Task PauseAndCancel_AreEnforcedByTheStateMachine_OnRealPostgres()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;
        await using var db = fixture.CreateContext();
        var normalizer = new UrlNormalizer();
        var store = new CrawlFrontierStore(db);
        var queue = new SeoJobQueue(db);
        var access = new ProjectAccessService(db);
        var quota = new QuotaService(db, Options.Create(new QuotaOptions()));
        var audit = new AuditLogService(db, access);
        var commands = new CrawlCommandService(db, store, queue, normalizer, access, quota, audit);

        var ownerId = Guid.NewGuid();
        var project = new SeoProject(ownerId, "Integration lifecycle", new Uri("https://example.com"));
        db.SeoProjects.Add(project);
        await db.SaveChangesAsync(ct);
        var crawl = await commands.StartAsync(project.Id, ownerId, ct);
        crawl.Should().NotBeNull();

        // A queued crawl has nothing to pause yet: the domain rejects it until
        // a worker picks the job up and flips the crawl to Running.
        var pauseWhileQueued = () => commands.PauseAsync(project.Id, crawl!.Id, ownerId, ct);
        await pauseWhileQueued.Should().ThrowAsync<InvalidOperationException>();

        crawl!.Start(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);

        (await commands.PauseAsync(project.Id, crawl.Id, ownerId, ct)).Should().BeTrue();
        var paused = await db.Crawls.SingleAsync(x => x.Id == crawl.Id, ct);
        paused.Status.Should().Be(CrawlStatus.Paused);

        (await commands.ResumeAsync(project.Id, crawl.Id, ownerId, ct)).Should().BeTrue();
        var resumed = await db.Crawls.SingleAsync(x => x.Id == crawl.Id, ct);
        resumed.Status.Should().Be(CrawlStatus.Running);
        (await db.SeoBackgroundJobs.CountAsync(x => x.IdempotencyKey.StartsWith($"continue-crawl:{crawl.Id}:resume:"), ct)).Should().Be(1);

        (await commands.CancelAsync(project.Id, crawl.Id, ownerId, ct)).Should().BeTrue();
        var cancelled = await db.Crawls.SingleAsync(x => x.Id == crawl.Id, ct);
        cancelled.Status.Should().Be(CrawlStatus.Cancelled);

        // Cross-user access: a different owner sees nothing.
        (await commands.PauseAsync(project.Id, crawl.Id, Guid.NewGuid(), ct)).Should().BeFalse();

        // Audit trail recorded for the owner only.
        var actions = await db.AuditLogs.Where(x => x.ProjectId == project.Id).Select(x => x.Action).ToListAsync(ct);
        actions.Should().Contain(new[] { "CRAWL_STARTED", "CRAWL_PAUSED", "CRAWL_RESUMED", "CRAWL_CANCELLED" });
    }
}

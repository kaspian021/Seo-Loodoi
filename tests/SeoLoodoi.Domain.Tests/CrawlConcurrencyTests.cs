using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Application.Urls;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F5 regression: the per-project Concurrency crawl setting must actually
/// bound how many page fetches the batch runner performs at the same time.
/// Today the loop is strictly sequential, so raising the setting changes
/// nothing while the UI advertises it as a real control.
/// </summary>
public sealed class CrawlConcurrencyTests
{
    private sealed class InMemoryFrontierStore : ICrawlFrontierStore
    {
        private readonly List<CrawlFrontierItem> _items = [];

        public Task<bool> EnqueueAsync(CrawlFrontierItem item, CancellationToken ct)
        {
            if (_items.Any(x => x.CrawlId == item.CrawlId && x.NormalizedUrl == item.NormalizedUrl)) return Task.FromResult(false);
            _items.Add(item);
            return Task.FromResult(true);
        }

        public Task<CrawlFrontierItem?> TryLeaseAsync(Guid crawlId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct)
        {
            var candidate = _items
                .Where(x => x.CrawlId == crawlId && (x.Status == FrontierStatus.Pending || (x.Status == FrontierStatus.Leased && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))))
                .OrderBy(x => x.Depth).ThenBy(x => x.CreatedAt)
                .FirstOrDefault();
            if (candidate is null || !candidate.TryLease(workerId, now, leaseDuration)) return Task.FromResult<CrawlFrontierItem?>(null);
            return Task.FromResult<CrawlFrontierItem?>(candidate);
        }

        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingJobQueue : ISeoJobQueue
    {
        public List<(SeoJobType Type, string Key)> Enqueued { get; } = [];

        public Task<Guid> EnqueueOnceAsync(SeoJobType type, string idempotencyKey, string payloadJson, CancellationToken ct, DateTimeOffset? notBefore = null)
        { Enqueued.Add((type, idempotencyKey)); return Task.FromResult(Guid.NewGuid()); }

        public Task<SeoBackgroundJob?> TryLeaseAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct) => Task.FromResult<SeoBackgroundJob?>(null);

        public Task SaveAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Fetches block until <see cref="ExpectedParallelism"/> of them are in
    /// flight simultaneously (or a short timeout elapses), recording the observed peak.</summary>
    private sealed class GatedFetcher(int expectedParallelism) : IPageFetcher, IConfigurablePageFetcher
    {
        private readonly object _gate = new();
        private int _current;
        public int MaxObserved { get; private set; }

        public Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, CancellationToken ct) =>
            FetchAsync(uri, maxResponseBytes, "test-bot", true, 20, ct);

        public async Task<FetchResult> FetchAsync(Uri uri, int maxResponseBytes, string userAgent, bool followRedirects, int timeoutSeconds, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(400);
            lock (_gate)
            {
                _current++;
                MaxObserved = Math.Max(MaxObserved, _current);
                Monitor.PulseAll(_gate);
            }
            lock (_gate)
            {
                while (_current < expectedParallelism && DateTimeOffset.UtcNow < deadline) Monitor.Wait(_gate, 50);
                _current--;
            }
            await Task.Delay(10, ct);
            var html = "<html><body><h1>Title</h1><p>content words</p></body></html>"u8.ToArray();
            return new FetchResult(uri, uri, 200, "text/html", new Dictionary<string, string[]>(), html, TimeSpan.FromMilliseconds(5), []);
        }
    }

    private sealed class EmptySitemaps : ISitemapDiscoveryService
    {
        public Task<SitemapDiscoveryResult> DiscoverAsync(IEnumerable<Uri> seeds, int maxDocuments, int maxUrls, CancellationToken ct) =>
            Task.FromResult(new SitemapDiscoveryResult([], [], [], false));
    }

    private sealed class StaticExtractor : IHtmlExtractor
    {
        public Task<ExtractedPage> ExtractAsync(string html, Uri pageUri, CancellationToken ct) =>
            Task.FromResult(new ExtractedPage("Title", null, [], null, null, null, "content words", 2, 0, 0, [], [], new Dictionary<string, string>(), new Dictionary<string, string>(), null));
    }

    private static async Task<(AppDbContext db, CrawlBatchRunner runner, GatedFetcher fetcher, RecordingJobQueue queue, SeoProject project, Crawl crawl, InMemoryFrontierStore frontier)> SetupAsync(int concurrency, int pageCount)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var settings = new CrawlSettings(MaxPages: 50, MaxDepth: 2, Concurrency: concurrency, DelayMilliseconds: 0, ObeyRobots: false).Validate();
        var project = new SeoProject(Guid.NewGuid(), "Concurrency", new Uri("https://concurrency.example"), settings);
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync();

        var frontier = new InMemoryFrontierStore();
        var normalizer = new UrlNormalizer();
        for (var i = 1; i <= pageCount; i++)
        {
            var url = $"https://concurrency.example/page-{i}";
            await frontier.EnqueueAsync(new CrawlFrontierItem(crawl.Id, project.Id, url, normalizer.Normalize(new Uri(url)).AbsoluteUri, 1), CancellationToken.None);
        }

        var fetcher = new GatedFetcher(expectedParallelism: Math.Min(concurrency, pageCount));
        var queue = new RecordingJobQueue();
        var planner = new CrawlFrontierPlanner(normalizer, frontier);
        var robots = new EmptyRobots();
        var runner = new CrawlBatchRunner(db, frontier, queue, fetcher, new StaticExtractor(), robots, new EmptySitemaps(), planner, normalizer, NullLogger<CrawlBatchRunner>.Instance);
        return (db, runner, fetcher, queue, project, crawl, frontier);
    }

    private sealed class EmptyRobots : IRobotsService
    {
        public Task<RobotsPolicy> GetPolicyAsync(Uri siteUri, CancellationToken ct) =>
            Task.FromResult(new RobotsPolicy(new RobotsDocument([], []), 200, DateTimeOffset.UtcNow, false));
    }

    private static SeoBackgroundJob Job(Crawl crawl, SeoProject project) =>
        new(SeoJobType.InitialCrawl, $"test:{crawl.Id}", JsonSerializer.Serialize(new { CrawlId = crawl.Id, ProjectId = project.Id }));

    [Fact]
    public async Task ConcurrencySetting_AllowsParallelFetches_UpToTheConfiguredBound()
    {
        var (db, runner, fetcher, _, project, crawl, _) = await SetupAsync(concurrency: 4, pageCount: 6);
        await using var _1 = db;

        await runner.RunAsync(Job(crawl, project), CancellationToken.None);

        fetcher.MaxObserved.Should().Be(4, "four page fetches must be in flight simultaneously when Concurrency is 4");
        (await db.CrawledUrls.CountAsync(x => x.Url.Contains("page-"))).Should().Be(6, "every seeded page is crawled");
    }

    [Fact]
    public async Task ConcurrencyOne_KeepsFetchesStrictlySequential()
    {
        var (db, runner, fetcher, _, project, crawl, _) = await SetupAsync(concurrency: 1, pageCount: 3);
        await using var _1 = db;

        await runner.RunAsync(Job(crawl, project), CancellationToken.None);

        fetcher.MaxObserved.Should().Be(1, "Concurrency=1 must never overlap fetches");
        (await db.CrawledUrls.CountAsync(x => x.Url.Contains("page-"))).Should().Be(3);
    }
}

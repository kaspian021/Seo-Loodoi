using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Crawling.Rendering;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Integration.Tests;

/// <summary>Crawler v2 D9 on real PostgreSQL. Render quota is reserved under the tenant advisory lock, so concurrent workers cannot overspend it.</summary>
[Collection("postgres")]
public sealed class CrawlerV2RenderQuotaPostgresTests(PostgresFixture fixture)
{
    private sealed class InstantRenderer : IPageRenderer
    {
        public int Calls;
        public bool IsAvailable => true;
        public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new RenderResult(true, RenderFailureKind.None, null, request.Url, "<html><head><title>R</title></head><body><h1>R</h1></body></html>", [], 0, TimeSpan.FromMilliseconds(5)));
        }
    }

    private const string Shell = "<html><head><title></title></head><body><div id=\"root\"></div></body></html>";

    [Fact]
    public async Task PerCrawlRenderCap5_20ConcurrentPages_ExactlyFiveCharged()
    {
        var owner = Guid.NewGuid();
        Guid projectId, crawlId;
        await using (var seed = fixture.CreateContext())
        {
            seed.TenantEntitlements.Add(new TenantEntitlement(owner, null, "Enterprise", SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29)));
            var project = new SeoProject(owner, "Render race", new Uri($"https://render-{owner:N}.example"), new CrawlSettings(RenderMode: "js", MaxRendersPerCrawl: 5));
            seed.SeoProjects.Add(project);
            var crawl = new Crawl(project.Id, CrawlTrigger.Manual); crawl.Start(DateTimeOffset.UtcNow); seed.Crawls.Add(crawl);
            await seed.SaveChangesAsync();
            projectId = project.Id; crawlId = crawl.Id;
        }

        var renderer = new InstantRenderer();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            await start.Task;
            await using var db = fixture.CreateContext();
            var project = await db.SeoProjects.SingleAsync(x => x.Id == projectId);
            var crawl = await db.Crawls.SingleAsync(x => x.Id == crawlId);
            var stage = new CrawlRenderStage(db, renderer, new HtmlExtractor(), new QuotaService(db, Options.Create(new QuotaOptions())), Options.Create(new RenderingOptions()), NullLogger<CrawlRenderStage>.Instance);
            var uri = new Uri($"https://render-{owner:N}.example/p{i}");
            var raw = await new HtmlExtractor().ExtractAsync(Shell, uri, default);
            var result = await stage.ProcessAsync(crawl, project, uri.AbsoluteUri, uri, Shell, raw, default);
            await db.SaveChangesAsync();
            return result.Rendered;
        })).ToArray();
        start.SetResult();
        var rendered = await Task.WhenAll(tasks);

        rendered.Count(x => x).Should().Be(5);
        renderer.Calls.Should().Be(5);
        await using var verify = fixture.CreateContext();
        (await verify.PageRenderEvidences.CountAsync(x => x.CrawlId == crawlId && x.Status == RenderEvidenceStatus.Rendered)).Should().Be(5);
        (await verify.PageRenderEvidences.CountAsync(x => x.CrawlId == crawlId && x.Status == RenderEvidenceStatus.QuotaExceeded)).Should().Be(15);
        (await new QuotaService(verify, Options.Create(new QuotaOptions())).GetAsync(owner, default)).RendersUsed.Should().Be(5);
    }
}

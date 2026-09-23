using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Crawling.Rendering;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Crawling.Rendering;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// Crawler v2 render orchestration with a scripted renderer. This covers HTML-first triggers, render quota
/// (per crawl and per plan), idempotent resume, failure, timeout, crash, backpressure fallbacks and client redirects.
/// The real Chromium path is covered by the PostgreSQL and browser suite in CI.
/// </summary>
public sealed class CrawlerV2RenderStageTests
{
    private const string Shell = "<html><head><title></title><script src='/_next/static/a.js'></script></head><body><div id=\"__next\"></div></body></html>";
    private static readonly string Hydrated = $"<html><head><title>Hydrated</title><link rel='canonical' href='https://spa.example/'></head><body><h1>Hello</h1><p>{string.Join(' ', Enumerable.Repeat("word", 150))}</p><a href='/deep'>deep</a></body></html>";

    private sealed class ScriptedRenderer(Func<RenderRequest, RenderResult> script, bool available = true) : IPageRenderer
    {
        public int Calls;
        public bool IsAvailable => available;
        public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(script(request)); }
    }

    private static RenderResult Ok(string html, Uri? final = null) => new(true, RenderFailureKind.None, null, final, html, [new RenderedResource("https://spa.example/a.js", "script", 200, 1234, false, null)], 0, TimeSpan.FromMilliseconds(40));

    private static async Task<(AppDbContext Db, SeoProject Project, Crawl Crawl)> SetupAsync(string renderMode = "js", int? maxRenders = null, string plan = "Pro", SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var owner = Guid.NewGuid();
        db.TenantEntitlements.Add(new TenantEntitlement(owner, null, plan, status, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29)));
        var project = new SeoProject(owner, "SPA", new Uri("https://spa.example"), new CrawlSettings(RenderMode: renderMode, MaxRendersPerCrawl: maxRenders));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual); crawl.Start(DateTimeOffset.UtcNow);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync();
        return (db, project, crawl);
    }

    private static CrawlRenderStage Stage(AppDbContext db, IPageRenderer renderer) =>
        new(db, renderer, new HtmlExtractor(), new QuotaService(db, Options.Create(new QuotaOptions())), Options.Create(new RenderingOptions()), NullLogger<CrawlRenderStage>.Instance);

    private static async Task<RenderStageResult> Run(CrawlRenderStage stage, AppDbContext db, SeoProject project, Crawl crawl, string url, string rawHtml)
    {
        var uri = new Uri(url);
        var raw = await new HtmlExtractor().ExtractAsync(rawHtml, uri, default);
        var result = await stage.ProcessAsync(crawl, project, uri.AbsoluteUri, uri, rawHtml, raw, default);
        await db.SaveChangesAsync();
        return result;
    }

    [Fact]
    public async Task HtmlMode_NeverRenders()
    {
        var (db, project, crawl) = await SetupAsync("html");
        var renderer = new ScriptedRenderer(_ => Ok(Hydrated));
        var result = await Run(Stage(db, renderer), db, project, crawl, "https://spa.example/", Shell);
        renderer.Calls.Should().Be(0); result.Evidence.Should().BeNull();
    }

    [Fact]
    public async Task JsMode_RendersSpa_UsesRenderedDom_AndStoresDiffEvidence()
    {
        var (db, project, crawl) = await SetupAsync("js");
        var result = await Run(Stage(db, new ScriptedRenderer(_ => Ok(Hydrated))), db, project, crawl, "https://spa.example/", Shell);
        result.Rendered.Should().BeTrue();
        result.Page.Title.Should().Be("Hydrated");
        result.RawPage.Title.Should().BeNull();
        var stored = await db.PageRenderEvidences.SingleAsync();
        stored.Status.Should().Be(RenderEvidenceStatus.Rendered);
        stored.CriticalDifferences.Should().BeGreaterThanOrEqualTo(4);   // title, canonical, h1, internal links, text
        stored.DiffJson.Should().Contain("\"field\":\"title\"").And.Contain("Hydrated");
        stored.ResourcesJson.Should().Contain("a.js");
        stored.TriggerSignalsJson.Should().Contain(RenderTriggers.EmptyAppShell);
    }

    [Fact]
    public async Task AutoMode_RendersOnlyWhenTriggersFire()
    {
        var (db, project, crawl) = await SetupAsync("auto");
        var renderer = new ScriptedRenderer(_ => Ok(Hydrated));
        var stage = Stage(db, renderer);
        await Run(stage, db, project, crawl, "https://spa.example/static", Hydrated);
        renderer.Calls.Should().Be(0, "a server-rendered page has no render triggers");
        await Run(stage, db, project, crawl, "https://spa.example/", Shell);
        renderer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task PerCrawlRenderCap_FallsBackToRawHtml_WithQuotaEvidence()
    {
        var (db, project, crawl) = await SetupAsync("js", maxRenders: 2);
        var renderer = new ScriptedRenderer(_ => Ok(Hydrated));
        var stage = Stage(db, renderer);
        for (var i = 0; i < 4; i++) await Run(stage, db, project, crawl, $"https://spa.example/p{i}", Shell);
        renderer.Calls.Should().Be(2);
        (await db.PageRenderEvidences.CountAsync(x => x.Status == RenderEvidenceStatus.QuotaExceeded)).Should().Be(2);
    }

    [Fact]
    public async Task InactiveSubscription_GetsNoRenders()
    {
        var (db, project, crawl) = await SetupAsync("js", status: SubscriptionStatus.Canceled);
        var renderer = new ScriptedRenderer(_ => Ok(Hydrated));
        var result = await Run(Stage(db, renderer), db, project, crawl, "https://spa.example/", Shell);
        renderer.Calls.Should().Be(0);
        result.Evidence!.Status.Should().Be(RenderEvidenceStatus.QuotaExceeded);
        result.Page.Title.Should().BeNull("raw HTML is used");
    }

    [Fact]
    public async Task MonthlyPlanQuota_CountsChargedRendersAcrossCrawls_AndIsReportedInUsage()
    {
        var (db, project, crawl) = await SetupAsync("js", plan: "Free");   // Free = 25 renders/month
        var limit = RenderQuotaPolicy.MonthlyRenders("Free", true);
        var old = new Crawl(project.Id, CrawlTrigger.Manual); db.Crawls.Add(old);
        for (var i = 0; i < limit; i++) db.PageRenderEvidences.Add(new PageRenderEvidence(old.Id, project.Id, $"https://spa.example/old{i}", "js", "desktop", "[]"));
        await db.SaveChangesAsync();
        var renderer = new ScriptedRenderer(_ => Ok(Hydrated));
        var result = await Run(Stage(db, renderer), db, project, crawl, "https://spa.example/", Shell);
        renderer.Calls.Should().Be(0);
        result.Evidence!.Reason.Should().Contain("Monthly render quota");
        var usage = await new QuotaService(db, Options.Create(new QuotaOptions())).GetAsync(project.OwnerId, default);
        usage.RendersPerMonth.Should().Be(limit); usage.RendersUsed.Should().Be(limit);
    }

    [Fact]
    public async Task Resume_ReprocessingSamePage_DoesNotChargeTwice()
    {
        var (db, project, crawl) = await SetupAsync("js");
        var stage = Stage(db, new ScriptedRenderer(_ => Ok(Hydrated)));
        await Run(stage, db, project, crawl, "https://spa.example/", Shell);
        await Run(stage, db, project, crawl, "https://spa.example/", Shell);   // lease expired → same item re-processed
        (await db.PageRenderEvidences.CountAsync()).Should().Be(1);
        (await new QuotaService(db, Options.Create(new QuotaOptions())).GetAsync(project.OwnerId, default)).RendersUsed.Should().Be(1);
    }

    [Theory]
    [InlineData(RenderFailureKind.Timeout)]
    [InlineData(RenderFailureKind.Crash)]
    [InlineData(RenderFailureKind.Oversized)]
    [InlineData(RenderFailureKind.NavigationBlocked)]
    public async Task RenderFailure_FallsBackToRawPage_AndRecordsFailure(RenderFailureKind kind)
    {
        var (db, project, crawl) = await SetupAsync("js");
        var result = await Run(Stage(db, new ScriptedRenderer(_ => RenderResult.Failed(kind, "boom", TimeSpan.FromSeconds(1)))), db, project, crawl, "https://spa.example/", Shell);
        result.Rendered.Should().BeFalse();
        result.Page.Should().BeSameAs(result.RawPage);
        result.Evidence!.Status.Should().Be(RenderEvidenceStatus.Failed);
        result.Evidence.Reason.Should().StartWith(kind.ToString());
    }

    [Fact]
    public async Task Backpressure_IsNotCharged()
    {
        var (db, project, crawl) = await SetupAsync("js");
        var result = await Run(Stage(db, new ScriptedRenderer(_ => throw new RenderCapacityException("Render queue is full."))), db, project, crawl, "https://spa.example/", Shell);
        result.Evidence!.Status.Should().Be(RenderEvidenceStatus.CapacityRejected);
        result.Evidence.IsCharged.Should().BeFalse();
    }

    [Fact]
    public async Task DisabledRenderer_RecordsDisabledEvidence_NeverPretends()
    {
        var (db, project, crawl) = await SetupAsync("js");
        var result = await Run(Stage(db, new DisabledPageRenderer()), db, project, crawl, "https://spa.example/", Shell);
        result.Evidence!.Status.Should().Be(RenderEvidenceStatus.Disabled);
        result.Rendered.Should().BeFalse();
    }

    [Fact]
    public async Task ClientRedirect_IsRecordedAsCriticalFinalUrlDifference()
    {
        var (db, project, crawl) = await SetupAsync("js");
        var result = await Run(Stage(db, new ScriptedRenderer(_ => Ok(Hydrated, new Uri("https://spa.example/landing")))), db, project, crawl, "https://spa.example/old", "<html><head><script>location.replace('/landing')</script></head></html>");
        result.ClientRedirectTarget.Should().Be(new Uri("https://spa.example/landing"));
        result.Diff!.Get("finalUrl")!.Severity.Should().Be(DiffSeverity.Critical);
    }

    [Fact]
    public async Task MobileViewport_IsPassedToRenderer()
    {
        var (db, project, _) = await SetupAsync("js");
        project.UpdateSettings(project.Settings with { Viewport = "mobile" });
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual); db.Crawls.Add(crawl); await db.SaveChangesAsync();
        RenderRequest? seen = null;
        await Run(Stage(db, new ScriptedRenderer(r => { seen = r; return Ok(Hydrated); })), db, project, crawl, "https://spa.example/", Shell);
        seen!.Viewport.IsMobile.Should().BeTrue();
        (await db.PageRenderEvidences.SingleAsync()).Viewport.Should().Be("mobile");
    }
}

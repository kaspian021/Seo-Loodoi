using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Aeo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Domain.Tests;

/// <summary>Persistence/access orchestration with the real analyzer; only outbound robots is a test double.</summary>
public sealed class Phase11AeoServiceTests
{
    private sealed class Robots(string? text, bool fail = false) : IRobotsService
    {
        public int Calls { get; private set; }
        public Task<RobotsPolicy> GetPolicyAsync(Uri uri, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new HttpRequestException("Test upstream failure");
            return Task.FromResult(new RobotsPolicy(new RobotsParser().Parse(text ?? "", uri), 200, DateTimeOffset.UtcNow, false, text));
        }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Viewer { get; } = Guid.NewGuid();
        public SeoProject Project { get; private set; } = null!;
        public Crawl Crawl { get; private set; } = null!;
        public async Task InitializeAsync()
        {
            Project = new SeoProject(Owner, "AEO test", new Uri("https://example.com"));
            Crawl = new Crawl(Project.Id, CrawlTrigger.Manual);
            Db.AddRange(Project, Crawl, new ProjectMember(Project.Id, Viewer, ProjectMemberRole.Viewer));
            await Db.SaveChangesAsync();
        }
        public AeoService Service(Robots robots) => new(Db, new ProjectAccessService(Db), new AeoAnalyzer(new RobotsParser()), robots);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ViewerOrOutsider_CannotAnalyzeOrFetchRobots(bool viewer)
    {
        await using var h = new Harness(); await h.InitializeAsync();
        var robots = new Robots("User-agent: *\nAllow: /");
        var service = h.Service(robots);
        var result = await service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, viewer ? h.Viewer : Guid.NewGuid(), default);
        Assert.Null(result);
        Assert.Equal(0, robots.Calls);
        Assert.Empty(await h.Db.AiVisibilitySnapshots.ToListAsync());
    }

    [Fact]
    public async Task ForeignCrawl_CannotBeAnalyzedUnderOwnedProject()
    {
        await using var h = new Harness(); await h.InitializeAsync();
        var other = new SeoProject(Guid.NewGuid(), "Other", new Uri("https://other.example"));
        var crawl = new Crawl(other.Id, CrawlTrigger.Manual);
        h.Db.AddRange(other, crawl); await h.Db.SaveChangesAsync();
        var robots = new Robots("");
        Assert.Null(await h.Service(robots).AnalyzeAsync(h.Project.Id, crawl.Id, h.Owner, default));
        Assert.Equal(0, robots.Calls);
    }

    [Fact]
    public async Task FailedRobotsAndEmptyCrawl_PersistUnknownScores_NotZero()
    {
        await using var h = new Harness(); await h.InitializeAsync();
        var service = h.Service(new Robots(null, fail: true));
        var result = await service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        Assert.NotNull(result);
        Assert.False(result.RobotsAvailable);
        Assert.Null(result.AiCrawlabilityScore);
        Assert.Null(result.AnswerReadinessScore);
        Assert.Null(result.CitationReadinessScore);
        Assert.Null(result.AiVisibilityScore);
        Assert.Equal(0, result.Signals.PagesAnalyzed);
        Assert.Single(await h.Db.AiVisibilitySnapshots.ToListAsync());
    }

    [Fact]
    public async Task Reanalysis_UpdatesSingleSnapshot_ViewerCanRead_OutsiderCannot()
    {
        await using var h = new Harness(); await h.InitializeAsync();
        var service = h.Service(new Robots("User-agent: *\nDisallow: /"));
        var first = await service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        var second = await service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        Assert.NotNull(first); Assert.NotNull(second);
        Assert.Equal(first.AiVisibilityScore, second.AiVisibilityScore);
        Assert.Single(await h.Db.AiVisibilitySnapshots.ToListAsync());
        var read = await service.LatestAsync(h.Project.Id, h.Crawl.Id, h.Viewer, default);
        Assert.NotNull(read);
        Assert.Equal(h.Project.Id, read.ProjectId);
        Assert.Equal(h.Crawl.Id, read.CrawlId);
        Assert.Equal(second.Signals, read.Signals);
        Assert.Null(await service.LatestAsync(h.Project.Id, h.Crawl.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task StoredPageEvidence_IsAnalyzed_WithoutImportingOtherProjectPages()
    {
        await using var h = new Harness(); await h.InitializeAsync();
        var page = new CrawledUrl(h.Crawl.Id, h.Project.Id, "https://example.com/guide", "https://example.com/guide", 200, "text/html", 1, 100, true, 200, null);
        var foreign = new CrawledUrl(h.Crawl.Id, Guid.NewGuid(), "https://foreign.example", null, 200, "text/html", 1, 100, true, 200, null);
        h.Db.AddRange(page, foreign);
        foreach (var p in new[] { page, foreign })
            h.Db.PageSnapshots.Add(new PageSnapshot(p.Id, h.Crawl.Id, "Guide", "Description", "What is SEO?", "[\"What is SEO?\"]", p.CanonicalUrl, null, "en", "[]", "An observed answer.", 0, 0, 0, 0));
        await h.Db.SaveChangesAsync();
        var result = await h.Service(new Robots(null)).AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        Assert.NotNull(result);
        Assert.Equal(1, result.Signals.PagesAnalyzed);
        Assert.NotNull(result.AnswerReadinessScore);
        Assert.NotNull(result.CitationReadinessScore);
        Assert.Null(result.AiCrawlabilityScore);
    }
}

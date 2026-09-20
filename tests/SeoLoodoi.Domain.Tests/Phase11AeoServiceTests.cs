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
            h.Db.PageSnapshots.Add(new PageSnapshot(p.Id, h.Crawl.Id, "Guide", "Description", "What is SEO?", "[{\"level\":1,\"text\":\"What is SEO?\"}]", p.CanonicalUrl, null, "en", "[]", "An observed answer.", 0, 0, 0, 0));
        await h.Db.SaveChangesAsync();
        var result = await h.Service(new Robots(null)).AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        Assert.NotNull(result);
        Assert.Equal(1, result.Signals.PagesAnalyzed);
        Assert.NotNull(result.AnswerReadinessScore);
        Assert.NotNull(result.CitationReadinessScore);
        Assert.Null(result.AiCrawlabilityScore);
    }
    private static async Task SeedCrawlerAsync(Harness h)
    {
        h.Db.AiCrawlerProfiles.Add(new AiCrawlerProfile("fixture-bot", "Fixture bot", "FixtureBot", AiCrawlerPurpose.AnswerEngine, 1m));
        await h.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task ObservedBlock_CreatesOneAdvisoryWithEvidence_AndReplayPreservesUserStatus()
    {
        await using var h = new Harness(); await h.InitializeAsync(); await SeedCrawlerAsync(h);
        var service = h.Service(new Robots("User-agent: FixtureBot\nDisallow: /"));
        await service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        var issue = Assert.Single(await h.Db.SeoIssues.ToListAsync());
        Assert.Equal(AeoIssueRules.Blocked, issue.RuleCode);
        Assert.Equal(IssueSeverity.Notice, issue.Severity);
        Assert.Contains("FixtureBot", issue.EvidenceJson);
        Assert.Contains("observedAt", issue.EvidenceJson);
        var id = issue.Id;
        issue.ChangeStatus(IssueStatus.Ignored); await h.Db.SaveChangesAsync();
        await service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        var again = Assert.Single(await h.Db.SeoIssues.ToListAsync());
        Assert.Equal(id, again.Id);
        Assert.Equal(IssueStatus.Ignored, again.Status);
        Assert.Empty(await h.Db.SeoScores.ToListAsync()); // advisory, not a fabricated SEO score
    }

    [Fact]
    public async Task UnavailableRecheck_DoesNotEraseEarlierObservedBlock()
    {
        await using var h = new Harness(); await h.InitializeAsync(); await SeedCrawlerAsync(h);
        await h.Service(new Robots("User-agent: FixtureBot\nDisallow: /"))
            .AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        var evidence = (await h.Db.SeoIssues.SingleAsync()).EvidenceJson;
        await h.Service(new Robots(null, fail: true)).AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        var retained = Assert.Single(await h.Db.SeoIssues.ToListAsync());
        Assert.Equal(evidence, retained.EvidenceJson);
        Assert.Equal(IssueStatus.Open, retained.Status);
    }

    [Fact]
    public async Task ObservedAllow_RemovesOnlyPreviouslyOpenAeoBlock()
    {
        await using var h = new Harness(); await h.InitializeAsync(); await SeedCrawlerAsync(h);
        await h.Service(new Robots("User-agent: FixtureBot\nDisallow: /"))
            .AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        var normal = new SeoIssue(h.Project.Id, h.Crawl.Id, null, "TITLE_MISSING", IssueSeverity.High, IssueCategory.OnPage, "Title", "Description", "{}");
        h.Db.SeoIssues.Add(normal); await h.Db.SaveChangesAsync();
        await h.Service(new Robots("User-agent: FixtureBot\nAllow: /"))
            .AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default);
        Assert.Equal(normal.Id, Assert.Single(await h.Db.SeoIssues.ToListAsync()).Id);
    }

    private sealed class CancelledRobots : IRobotsService
    {
        public Task<RobotsPolicy> GetPolicyAsync(Uri uri, CancellationToken ct) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task Cancellation_IsNotPersistedAsAnUnavailableAssessment()
    {
        await using var h = new Harness(); await h.InitializeAsync();
        var service = new AeoService(h.Db, new ProjectAccessService(h.Db), new AeoAnalyzer(new RobotsParser()), new CancelledRobots());
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AnalyzeAsync(h.Project.Id, h.Crawl.Id, h.Owner, default));
        Assert.Empty(await h.Db.AiVisibilitySnapshots.ToListAsync());
        Assert.Empty(await h.Db.SeoIssues.ToListAsync());
    }
}

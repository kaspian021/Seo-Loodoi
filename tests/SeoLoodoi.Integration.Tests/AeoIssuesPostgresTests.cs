using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SeoLoodoi.Application.Aeo;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Aeo;
using SeoLoodoi.Infrastructure.Analysis;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Integration.Tests;

[Collection("postgres")]
public sealed class AeoIssuesPostgresTests(PostgresFixture fixture)
{
    private sealed class Robots : IRobotsService
    {
        public Task<RobotsPolicy> GetPolicyAsync(Uri uri, CancellationToken ct)
        {
            const string text = "User-agent: *\nDisallow: /";
            return Task.FromResult(new RobotsPolicy(new RobotsParser().Parse(text, uri), 200, DateTimeOffset.UtcNow, false, text));
        }
    }
    private static AeoService Service(AppDbContext db) => new(db, new ProjectAccessService(db), new AeoAnalyzer(new RobotsParser()), new Robots());

    private async Task<(Guid project, Guid crawl, Guid owner)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var owner = Guid.NewGuid();
        var project = new SeoProject(owner, "AEO integration", new Uri("https://example.com"));
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow); crawl.Complete(DateTimeOffset.UtcNow);
        db.AddRange(project, crawl);
        await db.SaveChangesAsync();
        return (project.Id, crawl.Id, owner);
    }

    [Fact]
    public async Task ConcurrentAssessment_WritesOneSnapshotAndOneAdvisory_OnRealPostgres()
    {
        var (project, crawl, owner) = await SeedAsync();
        async Task Run()
        {
            await using var db = fixture.CreateContext();
            var report = await Service(db).AnalyzeAsync(project, crawl, owner, default);
            Assert.NotNull(report);
        }
        await Task.WhenAll(Run(), Run());
        await using var check = fixture.CreateContext();
        Assert.Equal(1, await check.AiVisibilitySnapshots.CountAsync(x => x.ProjectId == project && x.CrawlId == crawl));
        var issue = Assert.Single(await check.SeoIssues.Where(x => x.ProjectId == project && x.CrawlId == crawl).ToListAsync());
        Assert.Equal(AeoIssueRules.Blocked, issue.RuleCode);
        Assert.Contains("observedAt", issue.EvidenceJson);
        Assert.Null(await Service(check).LatestAsync(project, crawl, Guid.NewGuid(), default));
        Assert.NotNull(await Service(check).LatestAsync(project, crawl, owner, default));
    }

    [Fact]
    public async Task SeoAnalysisRetry_DoesNotDeleteAeoIssues_OrCountThemAsScoredRules()
    {
        var (project, crawl, owner) = await SeedAsync();
        Guid aeoIssue;
        await using (var db = fixture.CreateContext())
        {
            await Service(db).AnalyzeAsync(project, crawl, owner, default);
            aeoIssue = (await db.SeoIssues.SingleAsync(x => x.ProjectId == project)).Id;
            db.SeoIssues.Add(new SeoIssue(project, crawl, null, "TITLE_MISSING", IssueSeverity.High, IssueCategory.OnPage, "Old", "Old", "{}"));
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
        {
            var handler = new AnalyzeCrawlJobHandler(db, [], new ScoringEngine(), new ContentSimilarityEngine(), new InternalLinkGraph(), new UrlNormalizer(), NullLogger<AnalyzeCrawlJobHandler>.Instance);
            var job = new SeoBackgroundJob(SeoJobType.AnalyzeCrawl, $"test-aeo-seo:{crawl}", JsonSerializer.Serialize(new CrawlJobPayload(crawl, project)));
            await handler.HandleAsync(job, default);
        }
        await using var check = fixture.CreateContext();
        Assert.Equal(aeoIssue, (await check.SeoIssues.SingleAsync(x => x.ProjectId == project)).Id);
        Assert.Empty(await check.SeoScores.Where(x => x.ProjectId == project).ToListAsync());
        Assert.Equal(AnalysisStatus.NoData, (await check.CrawlAnalyses.SingleAsync(x => x.CrawlId == crawl)).Status);
    }
}

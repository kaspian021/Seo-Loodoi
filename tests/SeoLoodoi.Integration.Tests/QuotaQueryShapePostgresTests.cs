using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using AwesomeAssertions;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// F6 regression: the monthly page-quota count runs on every crawl start. It must
/// stay a single join-based statement (no correlated EXISTS subqueries per
/// CrawledUrls row) while keeping the counting semantics identical — owner
/// isolation, the calendar-month window, and the empty-ProjectId exclusion.
/// Executed on real PostgreSQL because query shape is provider-specific.
/// </summary>
[Collection("postgres")]
public sealed class QuotaQueryShapePostgresTests(PostgresFixture fixture)
{
    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Statements.Add(command.CommandText); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Statements.Add(command.CommandText); return ValueTask.FromResult(result); }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        { Statements.Add(command.CommandText); return result; }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { Statements.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private AppDbContext CreateContext(SqlCapture capture) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(capture).Options);

    private static Crawl AddStartedCrawl(AppDbContext db, SeoProject project, DateTimeOffset startedAt)
    {
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(startedAt);
        db.Crawls.Add(crawl);
        return crawl;
    }

    private static void AddUrl(AppDbContext db, Crawl crawl, SeoProject project, string url) =>
        db.CrawledUrls.Add(new CrawledUrl(crawl.Id, project.Id, url, null, 200, "text/html", 0, 10, true, 100, null));

    [Fact]
    public async Task PagesUsedQuery_IsJoinBased_NotCorrelatedExists()
    {
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            var projectA = new SeoProject(ownerA, "Quota shape A", new Uri("https://quota-a.example"));
            var projectB = new SeoProject(ownerB, "Quota shape B", new Uri("https://quota-b.example"));
            seed.SeoProjects.AddRange(projectA, projectB);
            var crawlA = AddStartedCrawl(seed, projectA, DateTimeOffset.UtcNow);
            var crawlB = AddStartedCrawl(seed, projectB, DateTimeOffset.UtcNow);
            for (var i = 0; i < 3; i++) AddUrl(seed, crawlA, projectA, $"https://quota-a.example/{i}");
            for (var i = 0; i < 2; i++) AddUrl(seed, crawlB, projectB, $"https://quota-b.example/{i}");
            await seed.SaveChangesAsync();
        }

        var capture = new SqlCapture();
        await using var db = CreateContext(capture);
        var quota = new QuotaService(db, Options.Create(new QuotaOptions()));

        var status = await quota.GetAsync(ownerA, CancellationToken.None);

        status.PagesUsed.Should().Be(3, "only the billing owner's current-month pages count");
        var pagesQueries = capture.Statements.Where(s => s.Contains("CrawledUrls", StringComparison.OrdinalIgnoreCase)).ToList();
        pagesQueries.Should().NotBeEmpty("the pages-used count must actually be issued");
        pagesQueries.Should().NotContain(s => s.Contains("EXISTS", StringComparison.OrdinalIgnoreCase),
            "the quota count must be join-based, not a correlated EXISTS subquery per row (F6)");
    }

    [Fact]
    public async Task GetAsync_IsolatesOwnersAndCountsOnlyCurrentMonth()
    {
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        await using (var seed = fixture.CreateContext())
        {
            var projectA = new SeoProject(ownerA, "Quota iso A", new Uri("https://quota-iso-a.example"));
            var archivedA = new SeoProject(ownerA, "Quota iso archived", new Uri("https://quota-iso-archived.example"));
            archivedA.Archive();
            var projectB = new SeoProject(ownerB, "Quota iso B", new Uri("https://quota-iso-b.example"));
            seed.SeoProjects.AddRange(projectA, archivedA, projectB);

            var thisMonth = AddStartedCrawl(seed, projectA, DateTimeOffset.UtcNow);
            var lastMonth = AddStartedCrawl(seed, projectA, DateTimeOffset.UtcNow.AddDays(-40));
            var archivedCrawl = AddStartedCrawl(seed, archivedA, DateTimeOffset.UtcNow);
            var crawlB = AddStartedCrawl(seed, projectB, DateTimeOffset.UtcNow);

            AddUrl(seed, thisMonth, projectA, "https://quota-iso-a.example/current/1");
            AddUrl(seed, thisMonth, projectA, "https://quota-iso-a.example/current/2");
            AddUrl(seed, lastMonth, projectA, "https://quota-iso-a.example/previous/1");
            AddUrl(seed, archivedCrawl, archivedA, "https://quota-iso-archived.example/current/1");
            for (var i = 0; i < 5; i++) AddUrl(seed, crawlB, projectB, $"https://quota-iso-b.example/{i}");
            seed.Keywords.Add(new Keyword(projectA.Id, "quota keyword"));
            seed.Keywords.Add(new Keyword(projectB.Id, "quota keyword b"));
            seed.Competitors.Add(new Competitor(projectA.Id, "Quota rival", new Uri("https://quota-rival.example")));
            await seed.SaveChangesAsync();
        }

        await using var db = fixture.CreateContext();
        var quota = new QuotaService(db, Options.Create(new QuotaOptions()));

        var status = await quota.GetAsync(ownerA, CancellationToken.None);

        status.PagesUsed.Should().Be(3, "current-month pages of all the owner's projects count; last month and other owners do not");
        status.KeywordsUsed.Should().Be(1);
        status.CompetitorsUsed.Should().Be(1);
        status.ProjectsUsed.Should().Be(1, "archived projects are excluded from the project count");
    }

    [Fact]
    public async Task EnsureCanStartCrawl_RespectsCurrentMonthUsage()
    {
        var owner = Guid.NewGuid();
        Guid projectId;
        await using (var seed = fixture.CreateContext())
        {
            var project = new SeoProject(owner, "Quota start", new Uri("https://quota-start.example"));
            seed.SeoProjects.Add(project);
            var crawl = AddStartedCrawl(seed, project, DateTimeOffset.UtcNow);
            for (var i = 0; i < 4; i++) AddUrl(seed, crawl, project, $"https://quota-start.example/{i}");
            await seed.SaveChangesAsync();
            projectId = project.Id;
        }

        await using var db = fixture.CreateContext();
        var tight = new QuotaOptions { PagesPerMonth = 5 };
        var quota = new QuotaService(db, Options.Create(tight));

        var allowed = () => quota.EnsureCanStartCrawlAsync(projectId, owner, requestedPages: 1, CancellationToken.None);
        await allowed.Should().NotThrowAsync();

        var blocked = () => quota.EnsureCanStartCrawlAsync(projectId, owner, requestedPages: 2, CancellationToken.None);
        await blocked.Should().ThrowAsync<QuotaExceededException>();
    }
}

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F-05 regression (unit half): the monthly page-quota boundary used by
/// EnsureCanStartCrawlAsync — reservation is whole-MaxPages by design, so
/// used + MaxPages == limit must be allowed and one page over must 429.
/// </summary>
public class QuotaServiceTests
{
    private const int MaxPages = 2;

    private static (AppDbContext Db, QuotaService Svc, Guid ProjectId, Guid Owner) Build(int pagesPerMonth, int usedPages, DateTimeOffset? crawlStartedAt = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"quota-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        var owner = Guid.NewGuid();
        var project = new SeoProject(owner, "Quota Project", new Uri("https://quota.example.com"));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        var started = crawlStartedAt ?? DateTimeOffset.UtcNow.AddDays(-1);
        crawl.Start(started);
        crawl.Complete(DateTimeOffset.UtcNow);
        db.Crawls.Add(crawl);
        for (var i = 0; i < usedPages; i++)
            db.CrawledUrls.Add(new CrawledUrl(crawl.Id, project.Id, $"https://quota.example.com/p{i}", null, 200, "text/html", 0, 50, true, 50, null));
        db.SaveChanges();
        var svc = new QuotaService(db, Options.Create(new QuotaOptions { PagesPerMonth = pagesPerMonth }));
        return (db, svc, project.Id, owner);
    }

    [Fact]
    public async Task Under_budget_crawl_start_is_allowed()
    {
        var (_, svc, project, owner) = Build(pagesPerMonth: 5, usedPages: 2);
        var act = async () => await svc.EnsureCanStartCrawlAsync(project, owner, MaxPages, CancellationToken.None);
        await act.Should().NotThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task At_exact_budget_boundary_crawl_start_is_allowed()
    {
        // used(3) + reserved MaxPages(2) == limit(5): the crawl may consume up to its cap.
        var (_, svc, project, owner) = Build(pagesPerMonth: 5, usedPages: 3);
        var act = async () => await svc.EnsureCanStartCrawlAsync(project, owner, MaxPages, CancellationToken.None);
        await act.Should().NotThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task One_page_over_budget_crawl_start_throws()
    {
        // used(4) + reserved MaxPages(2) = 6 > limit(5)
        var (_, svc, project, owner) = Build(pagesPerMonth: 5, usedPages: 4);
        var act = async () => await svc.EnsureCanStartCrawlAsync(project, owner, MaxPages, CancellationToken.None);
        await act.Should().ThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task Pages_from_a_previous_month_do_not_count()
    {
        var periodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).ToDateTime(TimeOnly.MinValue);
        var (_, svc, project, owner) = Build(pagesPerMonth: 1, usedPages: 4, crawlStartedAt: periodStart.AddDays(-2));
        var act = async () => await svc.EnsureCanStartCrawlAsync(project, owner, MaxPages, CancellationToken.None);
        await act.Should().NotThrowAsync<QuotaExceededException>("only the current calendar month counts");
    }

    [Fact]
    public async Task Pages_of_another_owner_do_not_count()
    {
        var (db, svc, _, _) = Build(pagesPerMonth: 1, usedPages: 4);
        // A second owner's project must not see the first owner's pages.
        var otherProject = new SeoProject(Guid.NewGuid(), "Other Owner Project", new Uri("https://other.example.com"));
        db.SeoProjects.Add(otherProject);
        db.SaveChanges();
        var act = async () => await svc.EnsureCanStartCrawlAsync(otherProject.Id, otherProject.OwnerId, MaxPages, CancellationToken.None);
        await act.Should().NotThrowAsync<QuotaExceededException>("quota is per billing owner");
    }

    [Fact]
    public async Task Unknown_project_is_a_no_op()
    {
        var (_, svc, _, _) = Build(pagesPerMonth: 1, usedPages: 4);
        var act = async () => await svc.EnsureCanStartCrawlAsync(Guid.NewGuid(), Guid.NewGuid(), MaxPages, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }
}

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Monitoring;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// F-02 regression: the CRITICAL_ISSUE alert rule used to be structurally
/// dead (no rule could emit a Critical issue). Now a 5xx page is a Critical
/// BROKEN_STATUS issue, so the rule must actually fire — and stay suppressed
/// within its 24h window.
/// </summary>
public class AlertServiceTests
{
    private static (AppDbContext Db, AlertService Svc, Guid Project, Guid Crawl) Build(IssueSeverity? criticalSeverity, out Guid owner)
    {
        owner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"alerts-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        var project = new SeoProject(owner, "Alert Project", new Uri("https://alerts.example.com"));
        db.SeoProjects.Add(project);
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        crawl.Start(DateTimeOffset.UtcNow.AddDays(-1));
        crawl.Complete(DateTimeOffset.UtcNow);
        db.Crawls.Add(crawl);
        if (criticalSeverity is not null)
            db.SeoIssues.Add(new SeoIssue(project.Id, crawl.Id, null, "BROKEN_STATUS", criticalSeverity.Value, IssueCategory.Technical, "HTTP 500", "Server error", "{\"statusCode\":\"500\"}"));
        db.AlertRules.Add(new AlertRule(project.Id, "CRITICAL_ISSUE", 0));
        db.SaveChanges();
        var svc = new AlertService(db, new AccessStub(), new OutboundUrlGuard(), new AuditStub());
        return (db, svc, project.Id, crawl.Id);
    }

    [Fact]
    public async Task Critical_issue_triggers_the_critical_issue_rule()
    {
        var (db, svc, project, _) = Build(IssueSeverity.Critical, out _);
        var created = await svc.CheckAsync(project, Guid.NewGuid(), CancellationToken.None);
        created.Should().Be(1, "an open Critical issue on the latest crawl must trigger the rule");
        (await db.AlertEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task High_severity_does_not_trigger_the_critical_issue_rule()
    {
        var (db, svc, project, _) = Build(IssueSeverity.High, out _);
        var created = await svc.CheckAsync(project, Guid.NewGuid(), CancellationToken.None);
        created.Should().Be(0, "the rule is specifically about Critical issues");
        (await db.AlertEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Second_check_within_24_hours_is_suppressed()
    {
        var (db, svc, project, _) = Build(IssueSeverity.Critical, out _);
        (await svc.CheckAsync(project, Guid.NewGuid(), CancellationToken.None)).Should().Be(1);
        (await svc.CheckAsync(project, Guid.NewGuid(), CancellationToken.None)).Should().Be(0, "24h dedup per rule+event type");
        (await db.AlertEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Crawl_failure_triggers_the_crawl_failure_rule()
    {
        var (db, svc, project, _) = Build(null, out _);
        db.AlertRules.Add(new AlertRule(project, "CRAWL_FAILURE", 0));
        var crawl = await db.Crawls.SingleAsync();
        crawl.Fail("fetch error", DateTimeOffset.UtcNow);
        db.SaveChanges();
        (await svc.CheckAsync(project, Guid.NewGuid(), CancellationToken.None)).Should().Be(1);
    }
}

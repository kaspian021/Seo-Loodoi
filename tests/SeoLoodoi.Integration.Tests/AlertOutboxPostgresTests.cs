using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Monitoring;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Projects;
using SeoLoodoi.Infrastructure.Security;
using AwesomeAssertions;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// Signed-webhook wiring on real PostgreSQL: a webhook rule is issued a secret,
/// and every delivery enqueued for it carries that secret so the worker can
/// sign the payload.
/// </summary>
[Collection("postgres")]
public sealed class AlertOutboxPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task WebhookRule_CheckEnqueuesDelivery_CarryingTheSigningSecret()
    {
        await using var db = fixture.CreateContext();
        var access = new ProjectAccessService(db);
        var audit = new AuditLogService(db, access);
        var alerts = new AlertService(db, access, new OutboundUrlGuard(), audit);

        var ownerId = Guid.NewGuid();
        var project = new SeoProject(ownerId, "Integration alerts", new Uri("https://example.com"));
        db.SeoProjects.Add(project);
        await db.SaveChangesAsync(CancellationToken.None);

        var rule = await alerts.CreateRuleAsync(project.Id, ownerId, new(
            Type: "CRAWL_FAILURE", Threshold: 0, Channel: "webhook", Destination: "https://example.com/hooks/seo"), CancellationToken.None);
        rule.Should().NotBeNull();
        rule!.WebhookSecret.Should().MatchRegex("^[0-9a-f]{64}$");

        // A failed crawl makes the CRAWL_FAILURE rule trigger on the next check.
        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync(CancellationToken.None);
        crawl.Fail("integration-forced failure", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);

        var created = await alerts.CheckAsync(project.Id, ownerId, CancellationToken.None);
        created.Should().Be(1);

        var delivery = await db.AlertDeliveries.SingleAsync(x => x.ProjectId == project.Id);
        delivery.Channel.Should().Be("webhook");
        delivery.WebhookSecret.Should().Be(rule.WebhookSecret, "the delivery must stay signable even if the rule changes later");
        delivery.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task DashboardRule_CheckEnqueuesDelivery_WithoutSecret()
    {
        await using var db = fixture.CreateContext();
        var access = new ProjectAccessService(db);
        var audit = new AuditLogService(db, access);
        var alerts = new AlertService(db, access, new OutboundUrlGuard(), audit);

        var ownerId = Guid.NewGuid();
        var project = new SeoProject(ownerId, "Integration alerts dashboard", new Uri("https://example.com"));
        db.SeoProjects.Add(project);
        await db.SaveChangesAsync(CancellationToken.None);

        var rule = await alerts.CreateRuleAsync(project.Id, ownerId, new(
            Type: "CRAWL_FAILURE", Threshold: 0, Channel: "dashboard", Destination: null), CancellationToken.None);
        rule!.WebhookSecret.Should().BeNull();

        var crawl = new Crawl(project.Id, CrawlTrigger.Manual);
        db.Crawls.Add(crawl);
        await db.SaveChangesAsync(CancellationToken.None);
        crawl.Fail("integration-forced failure", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(CancellationToken.None);

        var created = await alerts.CheckAsync(project.Id, ownerId, CancellationToken.None);

        // Dashboard alerts produce an event but no outbox delivery work at all.
        created.Should().Be(1);
        (await db.AlertEvents.CountAsync(x => x.ProjectId == project.Id)).Should().Be(1);
        (await db.AlertDeliveries.CountAsync(x => x.ProjectId == project.Id)).Should().Be(0);
    }
}

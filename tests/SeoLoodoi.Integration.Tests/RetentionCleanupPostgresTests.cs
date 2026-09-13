using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Jobs;
using AwesomeAssertions;

namespace SeoLoodoi.Integration.Tests;

/// <summary>
/// Retention must delete old bookkeeping rows and keep recent ones, on real
/// PostgreSQL rows whose timestamps are genuinely backdated.
/// </summary>
[Collection("postgres")]
public sealed class RetentionCleanupPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Cleanup_DeletesOnlyRowsOlderThanTheRetentionWindows()
    {
        await using var db = fixture.CreateContext();
        var handler = new CleanupJobHandler(db, Options.Create(new RetentionOptions
        {
            SucceededJobsRetentionDays = 30,
            FailedJobsRetentionDays = 90,
            AlertDeliveryRetentionDays = 30,
            AuditLogRetentionDays = 365,
        }), NullLogger<CleanupJobHandler>.Instance);
        var now = DateTimeOffset.UtcNow;

        var oldSucceeded = SucceededJob(SeoJobType.ContinueCrawl, $"cleanup-old-s:{Guid.NewGuid():N}", now);
        var recentSucceeded = SucceededJob(SeoJobType.ContinueCrawl, $"cleanup-new-s:{Guid.NewGuid():N}", now);
        var oldFailed = FailedJob($"cleanup-old-f:{Guid.NewGuid():N}", now);
        var midFailed = FailedJob($"cleanup-mid-f:{Guid.NewGuid():N}", now);
        // Running: never cleaned (not a terminal status) and never leasable by other
        // tests' TryLeaseAsync, so it cannot be stolen out of the shared database.
        var active = new SeoBackgroundJob(SeoJobType.ContinueCrawl, $"cleanup-active:{Guid.NewGuid():N}");
        active.TryLease("cleanup-test", now, TimeSpan.FromDays(1));
        db.SeoBackgroundJobs.AddRange(oldSucceeded, recentSucceeded, oldFailed, midFailed, active);

        static SeoBackgroundJob SucceededJob(SeoJobType type, string key, DateTimeOffset at)
        {
            var job = new SeoBackgroundJob(type, key);
            job.TryLease("cleanup-test", at, TimeSpan.FromMinutes(1));
            job.Succeed(at);
            return job;
        }
        static SeoBackgroundJob FailedJob(string key, DateTimeOffset at)
        {
            var job = new SeoBackgroundJob(SeoJobType.AnalyzeCrawl, key);
            job.TryLease("cleanup-test", at, TimeSpan.FromMinutes(1));
            job.Retry(at, TimeSpan.Zero, "terminal", 1);
            return job;
        }

        var projectId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var alertEvent = new AlertEvent(projectId, Guid.NewGuid(), "CRAWL_FAILURE", "{}");
        db.AlertEvents.Add(alertEvent);
        await db.SaveChangesAsync(CancellationToken.None);
        var oldDelivery = new AlertDelivery(projectId, alertEvent.Id, "webhook", "https://example.com/hook", "{}");
        oldDelivery.MarkDelivered();
        var recentDelivery = new AlertDelivery(projectId, alertEvent.Id, "webhook", "https://example.com/hook", "{}");
        recentDelivery.MarkDelivered();
        var pendingOld = new AlertDelivery(projectId, alertEvent.Id, "webhook", "https://example.com/hook", "{}");
        db.AlertDeliveries.AddRange(oldDelivery, recentDelivery, pendingOld);

        var oldAudit = new AuditLog(null, Guid.NewGuid(), "ACCOUNT_REGISTERED", "ApplicationUser");
        var recentAudit = new AuditLog(null, Guid.NewGuid(), "PROJECT_CREATED", "SeoProject");
        db.AuditLogs.AddRange(oldAudit, recentAudit);
        await db.SaveChangesAsync(CancellationToken.None);

        // Backdate beyond each retention window; keep control rows inside theirs.
        await db.SeoBackgroundJobs.Where(x => x.Id == oldSucceeded.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-31)));
        await db.SeoBackgroundJobs.Where(x => x.Id == oldFailed.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-91)));
        await db.SeoBackgroundJobs.Where(x => x.Id == midFailed.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-45)));
        await db.AlertDeliveries.Where(x => x.Id == oldDelivery.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-31)));
        await db.AlertDeliveries.Where(x => x.Id == pendingOld.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-200)));
        await db.AuditLogs.Where(x => x.Id == oldAudit.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddDays(-366)));

        await handler.HandleAsync(new SeoBackgroundJob(SeoJobType.Cleanup, $"cleanup-test:{Guid.NewGuid():N}"), CancellationToken.None);

        (await db.SeoBackgroundJobs.AnyAsync(x => x.Id == oldSucceeded.Id)).Should().BeFalse();
        (await db.SeoBackgroundJobs.AnyAsync(x => x.Id == oldFailed.Id)).Should().BeFalse();
        (await db.SeoBackgroundJobs.AnyAsync(x => x.Id == recentSucceeded.Id)).Should().BeTrue();
        (await db.SeoBackgroundJobs.AnyAsync(x => x.Id == midFailed.Id)).Should().BeTrue("45 days is inside the 90-day failed-job window");
        (await db.SeoBackgroundJobs.AnyAsync(x => x.Id == active.Id)).Should().BeTrue("queued or running jobs are never cleaned");

        (await db.AlertDeliveries.AnyAsync(x => x.Id == oldDelivery.Id)).Should().BeFalse();
        (await db.AlertDeliveries.AnyAsync(x => x.Id == recentDelivery.Id)).Should().BeTrue();
        (await db.AlertDeliveries.AnyAsync(x => x.Id == pendingOld.Id)).Should().BeTrue("undelivered work is never deleted by age alone");

        (await db.AuditLogs.AnyAsync(x => x.Id == oldAudit.Id)).Should().BeFalse();
        (await db.AuditLogs.AnyAsync(x => x.Id == recentAudit.Id)).Should().BeTrue();
    }
}

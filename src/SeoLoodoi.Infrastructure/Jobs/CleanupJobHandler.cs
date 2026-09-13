using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Jobs;

public sealed class RetentionOptions
{
    public int SucceededJobsRetentionDays { get; set; } = 30;
    public int FailedJobsRetentionDays { get; set; } = 90;
    public int AlertDeliveryRetentionDays { get; set; } = 30;
    public int AuditLogRetentionDays { get; set; } = 365;
}

/// <summary>
/// Retention is real cleanup, not a UI promise: terminal bookkeeping rows older
/// than the configured windows are deleted; business evidence (crawls, pages,
/// issues, scores, audit trail inside its window) is never touched.
/// </summary>
public sealed class CleanupJobHandler(AppDbContext db, IOptions<RetentionOptions> options, ILogger<CleanupJobHandler> logger) : ISeoJobHandler
{
    public SeoJobType Type => SeoJobType.Cleanup;

    public async Task HandleAsync(SeoBackgroundJob job, CancellationToken ct)
    {
        var retention = options.Value;
        var now = DateTimeOffset.UtcNow;
        var succeededCutoff = now.AddDays(-Math.Max(1, retention.SucceededJobsRetentionDays));
        var failedCutoff = now.AddDays(-Math.Max(1, retention.FailedJobsRetentionDays));
        var deliveryCutoff = now.AddDays(-Math.Max(1, retention.AlertDeliveryRetentionDays));
        var auditCutoff = now.AddDays(-Math.Max(1, retention.AuditLogRetentionDays));

        int removedJobs;
        int removedDeliveries;
        int removedAuditLogs;
        if (db.Database.IsRelational())
        {
            removedJobs = await db.SeoBackgroundJobs.Where(x => x.Status == SeoJobStatus.Succeeded && x.CreatedAt < succeededCutoff).ExecuteDeleteAsync(ct);
            removedJobs += await db.SeoBackgroundJobs.Where(x => (x.Status == SeoJobStatus.Failed || x.Status == SeoJobStatus.Cancelled) && x.CreatedAt < failedCutoff).ExecuteDeleteAsync(ct);
            removedDeliveries = await db.AlertDeliveries.Where(x => (x.Status == "Delivered" || x.Status == "DeadLetter") && x.CreatedAt < deliveryCutoff).ExecuteDeleteAsync(ct);
            removedAuditLogs = await db.AuditLogs.Where(x => x.CreatedAt < auditCutoff).ExecuteDeleteAsync(ct);
        }
        else
        {
            var oldJobs = await db.SeoBackgroundJobs
                .Where(x => (x.Status == SeoJobStatus.Succeeded && x.CreatedAt < succeededCutoff)
                    || ((x.Status == SeoJobStatus.Failed || x.Status == SeoJobStatus.Cancelled) && x.CreatedAt < failedCutoff))
                .ToListAsync(ct);
            db.SeoBackgroundJobs.RemoveRange(oldJobs);
            removedJobs = oldJobs.Count;
            var oldDeliveries = await db.AlertDeliveries.Where(x => (x.Status == "Delivered" || x.Status == "DeadLetter") && x.CreatedAt < deliveryCutoff).ToListAsync(ct);
            db.AlertDeliveries.RemoveRange(oldDeliveries);
            removedDeliveries = oldDeliveries.Count;
            var oldAuditLogs = await db.AuditLogs.Where(x => x.CreatedAt < auditCutoff).ToListAsync(ct);
            db.AuditLogs.RemoveRange(oldAuditLogs);
            removedAuditLogs = oldAuditLogs.Count;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Retention cleanup removed {Jobs} jobs, {Deliveries} alert deliveries and {AuditLogs} audit-log entries", removedJobs, removedDeliveries, removedAuditLogs);
    }
}

/// <summary>Enqueues the daily durable cleanup job; the queue makes repeats no-ops.</summary>
public sealed class CleanupWorker(IServiceScopeFactory scopes, ILogger<CleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScheduleAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ScheduleAsync(stoppingToken);
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<ISeoJobQueue>();
            await jobs.EnqueueOnceAsync(SeoJobType.Cleanup, $"cleanup:{DateTimeOffset.UtcNow:yyyy-MM-dd}", "{}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduling the daily retention cleanup failed");
        }
    }
}

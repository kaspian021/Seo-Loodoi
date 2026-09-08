using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Jobs;

/// <summary>
/// Revives crawls stuck in Queued/Running with no live crawl job behind them
/// (a lost continuation, a crash between batches, or rows written by an older
/// version). A running batch always shields itself through its own Running
/// job, so this sweeper only fires when progress is truly impossible.
/// </summary>
public sealed class StalledCrawlRecoveryService(IServiceScopeFactory scopes, ILogger<StalledCrawlRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await SweepAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Stalled crawl recovery sweep failed"); }
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Crawls.AsNoTracking()
            .Where(c => c.Status == CrawlStatus.Queued || c.Status == CrawlStatus.Running)
            .Select(c => new { c.Id, c.ProjectId })
            .ToListAsync(ct);
        if (pending.Count == 0) return;
        // Active jobs are few; match in memory to stay provider-agnostic.
        var activeKeys = await db.SeoBackgroundJobs.AsNoTracking()
            .Where(j => (j.Status == SeoJobStatus.Queued || j.Status == SeoJobStatus.Running)
                && (j.Type == SeoJobType.InitialCrawl || j.Type == SeoJobType.ContinueCrawl))
            .Select(j => j.IdempotencyKey)
            .ToListAsync(ct);
        var jobs = scope.ServiceProvider.GetRequiredService<ISeoJobQueue>();
        foreach (var crawl in pending)
        {
            // Every crawl-job key embeds its crawl id.
            if (activeKeys.Any(k => k.Contains(crawl.Id.ToString(), StringComparison.OrdinalIgnoreCase))) continue;
            var key = $"continue-crawl:{crawl.Id}:recovery:{Guid.NewGuid():N}";
            await jobs.EnqueueOnceAsync(SeoJobType.ContinueCrawl, key, JsonSerializer.Serialize(new CrawlJobPayload(crawl.Id, crawl.ProjectId)), ct);
            logger.LogWarning("Reviving stalled crawl {CrawlId}: no active crawl job found; recovery continuation enqueued", crawl.Id);
        }
    }
}

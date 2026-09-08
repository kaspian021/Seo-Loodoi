using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Analysis;

public sealed class AnalysisStatusService(AppDbContext db, IProjectAccessService access, ISeoJobQueue jobs, IAuditLogService audit, ILogger<AnalysisStatusService> logger) : IAnalysisStatusService
{
    public async Task<AnalysisStatusDto?> GetStatusAsync(Guid projectId, Guid userId, Guid crawlId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        var crawl = await db.Crawls.AsNoTracking().SingleOrDefaultAsync(x => x.Id == crawlId && x.ProjectId == projectId, ct);
        if (crawl is null) return null;
        var analysis = await db.CrawlAnalyses.SingleOrDefaultAsync(x => x.CrawlId == crawlId, ct);
        var job = await LatestAnalysisJobAsync(crawlId, ct);

        if (crawl.Status == CrawlStatus.Completed)
        {
            // Self-heal: a completed crawl must always have a durable analysis
            // row and an AnalyzeCrawl job. If the job went missing, enqueue it
            // again under the stable idempotency key.
            if (analysis is null)
            {
                analysis = new CrawlAnalysis(crawlId, projectId, $"analyze-crawl:{crawlId}");
                db.CrawlAnalyses.Add(analysis);
                await db.SaveChangesAsync(ct);
            }
            if (job is null && analysis.Status == AnalysisStatus.Pending)
            {
                await jobs.EnqueueOnceAsync(SeoJobType.AnalyzeCrawl, analysis.LastJobKey, JsonSerializer.Serialize(new CrawlJobPayload(crawlId, projectId)), ct);
                logger.LogWarning("Re-enqueued missing AnalyzeCrawl job for completed crawl {CrawlId}", crawlId);
                job = await LatestAnalysisJobAsync(crawlId, ct);
            }
        }

        var score = await db.SeoScores.AsNoTracking().Where(x => x.ProjectId == projectId && x.CrawlId == crawlId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new ScoreDto(x.OverallScore, x.TechnicalScore, x.IndexabilityScore, x.OnPageScore, x.ContentScore, x.LinksScore, x.StructuredDataScore, x.PerformanceScore, x.InternationalScore, x.SecurityScore, x.CalculationVersion, x.CreatedAt, x.CrawlId, x.IsPartial))
            .FirstOrDefaultAsync(ct);
        var issueCount = await db.SeoIssues.CountAsync(x => x.ProjectId == projectId && x.CrawlId == crawlId, ct);
        // Score and issue snapshots are only meaningful for this exact crawl.
        // Never fall back to another crawl's data here.
        if (score is not null && score.CrawlId != crawlId) score = null;
        return new AnalysisStatusDto(crawlId, projectId, crawl.Status.ToString(),
            analysis?.Status.ToString() ?? "Pending", job?.Status.ToString(), analysis?.Attempts ?? 0,
            analysis?.LastError ?? job?.LastError, analysis?.StartedAt, analysis?.FinishedAt,
            score, issueCount, IsRetryable(crawl, analysis, job));
    }

    public async Task<AnalysisStatusDto?> RetryAsync(Guid projectId, Guid userId, Guid crawlId, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;
        var crawl = await db.Crawls.SingleOrDefaultAsync(x => x.Id == crawlId && x.ProjectId == projectId, ct);
        if (crawl is null) return null;
        if (crawl.Status != CrawlStatus.Completed) throw new InvalidOperationException("Only a completed crawl can be re-analyzed.");
        var analysis = await db.CrawlAnalyses.SingleOrDefaultAsync(x => x.CrawlId == crawlId, ct);
        var job = await LatestAnalysisJobAsync(crawlId, ct);
        if (!IsRetryable(crawl, analysis, job)) throw new InvalidOperationException($"Analysis in state {analysis?.Status.ToString() ?? "none"} cannot be retried.");
        var retryKey = $"analyze-crawl:{crawlId}:retry:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        if (analysis is null)
        {
            analysis = new CrawlAnalysis(crawlId, projectId, retryKey);
            db.CrawlAnalyses.Add(analysis);
        }
        else analysis.ResetForRetry(retryKey, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueOnceAsync(SeoJobType.AnalyzeCrawl, retryKey, JsonSerializer.Serialize(new CrawlJobPayload(crawlId, projectId)), ct);
        await audit.RecordAsync(projectId, userId, "ANALYSIS_RETRIED", "Crawl", crawlId.ToString(), JsonSerializer.Serialize(new { retryKey }), null, ct);
        logger.LogInformation("Analysis retry enqueued for crawl {CrawlId} with key {RetryKey}", crawlId, retryKey);
        return await GetStatusAsync(projectId, userId, crawlId, ct);
    }

    private async Task<SeoBackgroundJob?> LatestAnalysisJobAsync(Guid crawlId, CancellationToken ct)
    {
        var prefix = $"analyze-crawl:{crawlId}";
        return await db.SeoBackgroundJobs.AsNoTracking()
            .Where(x => x.Type == SeoJobType.AnalyzeCrawl && (x.IdempotencyKey == prefix || x.IdempotencyKey.StartsWith(prefix + ":")))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
    }

    private static bool IsRetryable(Crawl crawl, CrawlAnalysis? analysis, SeoBackgroundJob? job)
    {
        if (crawl.Status != CrawlStatus.Completed) return false;
        if (analysis?.Status == AnalysisStatus.Failed) return true;
        if (job?.Status == SeoJobStatus.Failed && analysis?.Status != AnalysisStatus.Succeeded) return true;
        // A completed crawl with no analysis row and no job is self-healed by
        // GetStatusAsync, but an explicit retry must also be accepted.
        if (analysis is null && job is null) return true;
        return false;
    }
}

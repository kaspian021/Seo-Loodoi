namespace SeoLoodoi.Application.Analysis;

/// <summary>
/// Tenant-scoped analysis lifecycle for one crawl. The frontend polls this
/// endpoint while the crawl is active or the analysis is Pending/Running, and
/// refreshes score/issues/recommendations/history only after Succeeded.
/// </summary>
public sealed record AnalysisStatusDto(
    Guid CrawlId,
    Guid ProjectId,
    string CrawlStatus,
    string AnalysisStatus,
    string? JobStatus,
    int Attempts,
    string? LastError,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ScoreDto? Score,
    int IssueCount,
    bool Retryable);

public interface IAnalysisStatusService
{
    /// <summary>
    /// Returns null when the project/crawl is not visible to the caller.
    /// Self-heals a completed crawl whose AnalyzeCrawl job went missing by
    /// re-enqueueing it under the stable idempotency key.
    /// </summary>
    Task<AnalysisStatusDto?> GetStatusAsync(Guid projectId, Guid userId, Guid crawlId, CancellationToken ct);
    /// <summary>
    /// Re-enqueues analysis for a completed crawl whose analysis failed.
    /// Returns null when not visible; throws InvalidOperationException (mapped
    /// to 409) when the analysis is not in a retryable state.
    /// </summary>
    Task<AnalysisStatusDto?> RetryAsync(Guid projectId, Guid userId, Guid crawlId, CancellationToken ct);
}

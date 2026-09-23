using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>Outcome of the JavaScript rendering step for one page (Crawler v2, D2/D3/D9).</summary>
public enum RenderEvidenceStatus
{
    /// <summary>Quota was reserved; the render has not finished yet. Counts against render quota.</summary>
    Reserved = 0,
    Rendered = 1,
    /// <summary>The render ran and failed (timeout, crash, oversized DOM). Counts against quota.</summary>
    Failed = 2,
    /// <summary>Plan or project render quota exhausted; the raw HTML is used. Not charged.</summary>
    QuotaExceeded = 3,
    /// <summary>Renderer queue full (backpressure) or circuit open; the raw HTML is used. Not charged.</summary>
    CapacityRejected = 4,
    /// <summary>Rendering is not enabled in this deployment. Not charged.</summary>
    Disabled = 5
}

/// <summary>
/// Stored, deterministic evidence of how the rendered DOM differs from the raw
/// HTML response. One row per crawl and normalized URL, so a resumed batch reuses
/// its reservation instead of charging the quota twice (D7 idempotency).
/// </summary>
public sealed class PageRenderEvidence : Entity
{
    private PageRenderEvidence() { }
    public PageRenderEvidence(Guid crawlId, Guid projectId, string normalizedUrl, string renderMode, string viewport, string triggerSignalsJson)
    {
        if (crawlId == Guid.Empty || projectId == Guid.Empty) throw new ArgumentException("Crawl and project are required.");
        CrawlId = crawlId; ProjectId = projectId; NormalizedUrl = normalizedUrl[..Math.Min(normalizedUrl.Length, 2048)];
        RenderMode = renderMode; Viewport = viewport; TriggerSignalsJson = triggerSignalsJson;
    }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid? CrawledUrlId { get; private set; }
    public string NormalizedUrl { get; private set; } = string.Empty;
    public string RenderMode { get; private set; } = "html";
    public string Viewport { get; private set; } = "desktop";
    public RenderEvidenceStatus Status { get; private set; } = RenderEvidenceStatus.Reserved;
    public string? Reason { get; private set; }
    public string TriggerSignalsJson { get; private set; } = "[]";
    public string DiffJson { get; private set; } = "{}";
    public string ResourcesJson { get; private set; } = "[]";
    public string? RenderedFinalUrl { get; private set; }
    public int RenderedWordCount { get; private set; }
    public int RawWordCount { get; private set; }
    public int CriticalDifferences { get; private set; }
    public int SubresourceRequests { get; private set; }
    public int BlockedRequests { get; private set; }
    public int JsErrors { get; private set; }
    public long DurationMs { get; private set; }

    public bool IsCharged => Status is RenderEvidenceStatus.Reserved or RenderEvidenceStatus.Rendered or RenderEvidenceStatus.Failed;

    /// <summary>Re-arms a row whose earlier attempt was not charged (quota, capacity, disabled) for a new charged attempt.</summary>
    public void Reserve(DateTimeOffset now) { Status = RenderEvidenceStatus.Reserved; Reason = null; UpdatedAt = now; }
    public void MarkRendered(string? finalUrl, int rawWords, int renderedWords, int criticalDifferences, string diffJson, string resourcesJson, int subresources, int blocked, int jsErrors, long durationMs, DateTimeOffset now)
    {
        Status = RenderEvidenceStatus.Rendered; Reason = null; RenderedFinalUrl = finalUrl?[..Math.Min(finalUrl.Length, 2048)];
        RawWordCount = rawWords; RenderedWordCount = renderedWords; CriticalDifferences = criticalDifferences; DiffJson = diffJson; ResourcesJson = resourcesJson;
        SubresourceRequests = subresources; BlockedRequests = blocked; JsErrors = jsErrors; DurationMs = durationMs; UpdatedAt = now;
    }
    public void MarkNotRendered(RenderEvidenceStatus status, string reason, long durationMs, DateTimeOffset now)
    {
        if (status is RenderEvidenceStatus.Rendered or RenderEvidenceStatus.Reserved) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status; Reason = reason[..Math.Min(reason.Length, 1000)]; DurationMs = durationMs; UpdatedAt = now;
    }
    public void AttachTo(Guid crawledUrlId, DateTimeOffset now) { CrawledUrlId = crawledUrlId; UpdatedAt = now; }
}

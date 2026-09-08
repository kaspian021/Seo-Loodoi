using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

public sealed class Crawl : Entity
{
    private Crawl() { }
    public Crawl(Guid projectId, CrawlTrigger trigger)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        ProjectId = projectId; Trigger = trigger;
    }
    public Guid ProjectId { get; private set; }
    public CrawlStatus Status { get; private set; } = CrawlStatus.Queued;
    public CrawlTrigger Trigger { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public DateTimeOffset? HeartbeatAt { get; private set; }
    public int PagesDiscovered { get; private set; }
    public int PagesCrawled { get; private set; }
    public int Errors { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void Start(DateTimeOffset now)
    {
        if (Status is not (CrawlStatus.Queued or CrawlStatus.Paused)) throw new InvalidOperationException($"Cannot start crawl from {Status}.");
        Status = CrawlStatus.Running; StartedAt ??= now; HeartbeatAt = now; UpdatedAt = now;
    }
    public void Heartbeat(DateTimeOffset now) { if (Status != CrawlStatus.Running) throw new InvalidOperationException("Only a running crawl can heartbeat."); HeartbeatAt = now; UpdatedAt = now; }
    public void Pause(DateTimeOffset now) { if (Status != CrawlStatus.Running) throw new InvalidOperationException("Only a running crawl can pause."); Status = CrawlStatus.Paused; UpdatedAt = now; }
    public void Cancel(DateTimeOffset now) { if (Status is CrawlStatus.Completed or CrawlStatus.Cancelled) return; Status = CrawlStatus.Cancelled; FinishedAt = now; UpdatedAt = now; }
    public void Complete(DateTimeOffset now) { if (Status != CrawlStatus.Running) throw new InvalidOperationException("Only a running crawl can complete."); Status = CrawlStatus.Completed; FinishedAt = now; UpdatedAt = now; }
    public void Fail(string error, DateTimeOffset now) { Status = CrawlStatus.Failed; ErrorMessage = error[..Math.Min(error.Length, 2000)]; FinishedAt = now; UpdatedAt = now; }
    public void ReportDiscovered(int count = 1) { if (count < 0) throw new ArgumentOutOfRangeException(nameof(count)); PagesDiscovered += count; }
    public void ReportCrawled(bool failed = false) { PagesCrawled++; if (failed) Errors++; }
    public void ReportError() { Errors++; }
}

public sealed class CrawledUrl : Entity
{
    private CrawledUrl() { }
    public CrawledUrl(Guid crawlId, Guid projectId, string url, string? canonicalUrl, int statusCode, string? contentType, int depth, long responseTimeMs, bool isIndexable, int wordCount, string? contentHash, string? redirectChainJson = null, string? headersJson = null)
    { CrawlId = crawlId; ProjectId = projectId; Url = url; CanonicalUrl = canonicalUrl; StatusCode = statusCode; ContentType = contentType; Depth = depth; ResponseTimeMs = responseTimeMs; IsIndexable = isIndexable; WordCount = wordCount; ContentHash = contentHash; RedirectChainJson = redirectChainJson ?? "[]"; HeadersJson = headersJson ?? "{}"; }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string? CanonicalUrl { get; private set; }
    public int StatusCode { get; private set; }
    public string? ContentType { get; private set; }
    public int Depth { get; private set; }
    public long ResponseTimeMs { get; private set; }
    public bool IsIndexable { get; private set; }
    public int WordCount { get; private set; }
    public string? ContentHash { get; private set; }
    public string RedirectChainJson { get; private set; } = "[]";
    public string HeadersJson { get; private set; } = "{}";
}

public sealed class PageSnapshot : Entity
{
    private PageSnapshot() { }
    public PageSnapshot(Guid crawledUrlId, Guid crawlId, string? title, string? metaDescription, string? h1, string headingsJson, string? canonical, string? robotsMeta, string? language, string schemaJson, string textContent, int imageCount, int missingAltCount, int internalLinkCount, int externalLinkCount, string? hreflangJson = null, string? openGraphJson = null, string? twitterCardsJson = null, string? xRobotsTag = null)
    { CrawledUrlId = crawledUrlId; CrawlId = crawlId; Title = title; MetaDescription = metaDescription; H1 = h1; HeadingsJson = headingsJson; Canonical = canonical; RobotsMeta = robotsMeta; Language = language; SchemaJson = schemaJson; TextContent = textContent; ContentLength = textContent.Length; ImageCount = imageCount; MissingAltCount = missingAltCount; InternalLinkCount = internalLinkCount; ExternalLinkCount = externalLinkCount; HreflangJson = hreflangJson ?? "[]"; OpenGraphJson = openGraphJson ?? "{}"; TwitterCardsJson = twitterCardsJson ?? "{}"; XRobotsTag = xRobotsTag; }
    public Guid CrawledUrlId { get; private set; }
    public Guid CrawlId { get; private set; }
    public string? Title { get; private set; }
    public string? MetaDescription { get; private set; }
    public string? H1 { get; private set; }
    public string HeadingsJson { get; private set; } = "[]";
    public string? Canonical { get; private set; }
    public string? RobotsMeta { get; private set; }
    public string? Language { get; private set; }
    public string SchemaJson { get; private set; } = "[]";
    public string TextContent { get; private set; } = string.Empty;
    public int ContentLength { get; private set; }
    public int ImageCount { get; private set; }
    public int MissingAltCount { get; private set; }
    public int InternalLinkCount { get; private set; }
    public int ExternalLinkCount { get; private set; }
    public string HreflangJson { get; private set; } = "[]";
    public string OpenGraphJson { get; private set; } = "{}";
    public string TwitterCardsJson { get; private set; } = "{}";
    public string? XRobotsTag { get; private set; }
}

public sealed class PageLink : Entity
{
    private PageLink() { }
    public PageLink(Guid crawlId, Guid sourceUrlId, string targetUrl, string normalizedTarget, string? anchorText, string? rel, bool isInternal, bool isNofollow)
    { CrawlId = crawlId; SourceUrlId = sourceUrlId; TargetUrl = targetUrl; NormalizedTarget = normalizedTarget; AnchorText = anchorText; Rel = rel; IsInternal = isInternal; IsNofollow = isNofollow; }
    public Guid CrawlId { get; private set; }
    public Guid SourceUrlId { get; private set; }
    public string TargetUrl { get; private set; } = string.Empty;
    public string NormalizedTarget { get; private set; } = string.Empty;
    public string? AnchorText { get; private set; }
    public string? Rel { get; private set; }
    public bool IsInternal { get; private set; }
    public bool IsNofollow { get; private set; }
}

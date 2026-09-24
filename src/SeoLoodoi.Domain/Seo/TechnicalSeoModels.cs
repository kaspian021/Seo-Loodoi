using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>
/// Persisted robots.txt evidence for one crawl (P1 Phase 6). The analysis layer
/// re-evaluates blocking decisions purely from this stored raw text — it never
/// refetches the network. One row per crawl.
/// </summary>
public sealed class CrawlRobotsEvidence : Entity
{
    private CrawlRobotsEvidence() { }
    public CrawlRobotsEvidence(Guid crawlId, Guid projectId, string? rawText, int? httpStatus, bool temporarilyUnavailable, DateTimeOffset fetchedAt)
    {
        if (crawlId == Guid.Empty || projectId == Guid.Empty) throw new ArgumentException("Crawl and project are required.");
        CrawlId = crawlId; ProjectId = projectId;
        RawText = rawText is null ? null : rawText[..Math.Min(rawText.Length, 262144)];
        HttpStatus = httpStatus; TemporarilyUnavailable = temporarilyUnavailable; FetchedAt = fetchedAt;
    }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string? RawText { get; private set; }
    public int? HttpStatus { get; private set; }
    public bool TemporarilyUnavailable { get; private set; }
    public DateTimeOffset FetchedAt { get; private set; }
}

/// <summary>
/// Discovery outcome of the XML sitemap walk for one crawl (P1 Phase 5): which
/// sitemap documents were processed (with kind and errors), in JSON, plus the
/// truncation flag. One row per crawl.
/// </summary>
public sealed class CrawlSitemapState : Entity
{
    private CrawlSitemapState() { }
    public CrawlSitemapState(Guid crawlId, Guid projectId, string seedsJson, string processedSitemapsJson, string errorsJson, bool truncated)
    {
        if (crawlId == Guid.Empty || projectId == Guid.Empty) throw new ArgumentException("Crawl and project are required.");
        CrawlId = crawlId; ProjectId = projectId;
        SeedsJson = seedsJson; ProcessedSitemapsJson = processedSitemapsJson; ErrorsJson = errorsJson; Truncated = truncated;
    }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    /// <summary>JSON array of the sitemap URLs the discovery started from.</summary>
    public string SeedsJson { get; private set; } = "[]";
    /// <summary>JSON array of <c>{ url, kind }</c> per processed sitemap document (kind: urlset | index).</summary>
    public string ProcessedSitemapsJson { get; private set; } = "[]";
    /// <summary>JSON array of parse/fetch error strings collected during discovery.</summary>
    public string ErrorsJson { get; private set; } = "[]";
    public bool Truncated { get; private set; }
}

/// <summary>
/// One URL declared by the crawl's XML sitemaps (P1 Phase 5). Unique per
/// (CrawlId, NormalizedUrl) so a retried seed pass is idempotent. The coverage
/// analysis compares these against what was actually crawled.
/// </summary>
public sealed class CrawlSitemapEntry : Entity
{
    private CrawlSitemapEntry() { }
    public CrawlSitemapEntry(Guid crawlId, Guid projectId, string normalizedUrl, DateTimeOffset? lastModified, string? changeFrequency, decimal? priority)
    {
        if (crawlId == Guid.Empty || projectId == Guid.Empty) throw new ArgumentException("Crawl and project are required.");
        CrawlId = crawlId; ProjectId = projectId;
        NormalizedUrl = normalizedUrl[..Math.Min(normalizedUrl.Length, 2048)];
        LastModified = lastModified;
        ChangeFrequency = changeFrequency is null ? null : changeFrequency[..Math.Min(changeFrequency.Length, 32)];
        Priority = priority;
    }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string NormalizedUrl { get; private set; } = string.Empty;
    public DateTimeOffset? LastModified { get; private set; }
    public string? ChangeFrequency { get; private set; }
    /// <summary>Advisory sitemap priority hint (0.0–1.0). Not a ranking factor.</summary>
    public decimal? Priority { get; private set; }
}

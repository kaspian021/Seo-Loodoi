namespace SeoLoodoi.Application.Crawling;

public sealed record RobotsPolicy(RobotsDocument Document, int? HttpStatus, DateTimeOffset FetchedAt, bool TemporarilyUnavailable)
{
    public bool CanCrawl(string userAgent, Uri uri) => !TemporarilyUnavailable && Document.IsAllowed(userAgent, uri);
}
public interface IRobotsService { Task<RobotsPolicy> GetPolicyAsync(Uri siteUri, CancellationToken ct); }

public sealed record SitemapDiscoveryResult(IReadOnlyList<SitemapEntry> Urls, IReadOnlyList<Uri> ProcessedSitemaps, IReadOnlyList<string> Errors, bool Truncated);
public interface ISitemapDiscoveryService
{
    Task<SitemapDiscoveryResult> DiscoverAsync(IEnumerable<Uri> seeds, int maxDocuments, int maxUrls, CancellationToken ct);
}

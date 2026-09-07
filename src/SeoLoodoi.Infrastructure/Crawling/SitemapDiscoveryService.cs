using System.IO.Compression;
using SeoLoodoi.Application.Crawling;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class SitemapDiscoveryService(IPageFetcher fetcher, ISitemapParser parser) : ISitemapDiscoveryService
{
    public async Task<SitemapDiscoveryResult> DiscoverAsync(IEnumerable<Uri> seeds, int maxDocuments, int maxUrls, CancellationToken ct)
    {
        if (maxDocuments is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxDocuments));
        if (maxUrls is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxUrls));
        var queue = new Queue<Uri>(seeds.Where(IsHttp));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urls = new Dictionary<string, SitemapEntry>(StringComparer.OrdinalIgnoreCase);
        var processed = new List<Uri>(); var errors = new List<string>(); var truncated = false;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (processed.Count >= maxDocuments || urls.Count >= maxUrls) { truncated = true; break; }
            var sitemapUri = queue.Dequeue();
            if (!seen.Add(sitemapUri.AbsoluteUri)) continue;
            try
            {
                var response = await fetcher.FetchAsync(sitemapUri, 5_000_000, ct);
                if (response.StatusCode is < 200 or >= 300) { errors.Add($"{sitemapUri}: HTTP {response.StatusCode}"); continue; }
                await using var content = new MemoryStream(response.Content, writable: false);
                await using Stream payload = IsGzip(sitemapUri, response) ? new GZipStream(content, CompressionMode.Decompress) : content;
                var parsed = parser.Parse(payload, sitemapUri); processed.Add(sitemapUri);
                if (parsed.Kind == SitemapKind.Index)
                {
                    foreach (var child in parsed.Entries.Select(x => x.Location).Where(IsHttp)) if (!seen.Contains(child.AbsoluteUri)) queue.Enqueue(child);
                }
                else
                {
                    foreach (var entry in parsed.Entries)
                    {
                        if (urls.Count >= maxUrls) { truncated = true; break; }
                        urls.TryAdd(entry.Location.AbsoluteUri, entry);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or HttpRequestException)
            {
                errors.Add($"{sitemapUri}: {ex.GetType().Name}");
            }
        }
        return new(urls.Values.ToArray(), processed, errors, truncated);
    }
    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https";
    private static bool IsGzip(Uri uri, FetchResult response) => uri.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || string.Equals(response.ContentType, "application/gzip", StringComparison.OrdinalIgnoreCase) || string.Equals(response.ContentType, "application/x-gzip", StringComparison.OrdinalIgnoreCase);
}

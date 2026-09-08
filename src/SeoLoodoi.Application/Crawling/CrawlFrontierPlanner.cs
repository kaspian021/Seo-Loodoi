using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Crawling;

public sealed class CrawlFrontierPlanner(IUrlNormalizer normalizer, ICrawlFrontierStore store)
{
    public async Task<int> EnqueueDiscoveredAsync(Guid crawlId, Guid projectId, Uri projectBaseUri, IEnumerable<Uri> discovered, int depth, int maxDepth, bool includeSubdomains, Guid? sourceId, CancellationToken ct)
    {
        if (depth > maxDepth) return 0;
        var accepted = 0;
        foreach (var candidate in discovered)
        {
            ct.ThrowIfCancellationRequested();
            if (!candidate.IsAbsoluteUri || candidate.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(candidate.UserInfo) || !HostAllowed(projectBaseUri, candidate, includeSubdomains)) continue;
            if (IsWildcardQuery(candidate)) continue;
            Uri normalized;
            try { normalized = normalizer.Normalize(candidate); } catch (ArgumentException) { continue; }
            if (await store.EnqueueAsync(new CrawlFrontierItem(crawlId, projectId, candidate.AbsoluteUri, normalized.AbsoluteUri, depth, sourceId), ct)) accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Templates such as <c>https://host/search?*</c> are wildcard placeholders,
    /// not real pages. Crawling them wastes budget and pollutes evidence.
    /// </summary>
    internal static bool IsWildcardQuery(Uri candidate)
    {
        var query = candidate.Query;
        if (string.IsNullOrEmpty(query) || query == "?") return false;
        var body = query[1..].Trim();
        if (body is "*" or "%2A" or "%2a") return true;
        // "?*=..." or "?foo=*" carry no real parameter value either.
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = pair.Trim();
            var key = segment.Split('=', 2)[0].Trim();
            var value = segment.Contains('=') ? segment.Split('=', 2)[1].Trim() : string.Empty;
            if (key is "*" or "%2A" or "%2a") return true;
            if (value is "*" or "%2A" or "%2a") return true;
        }
        return false;
    }

    internal static bool HostAllowed(Uri project, Uri candidate, bool subdomains)
    {
        if (string.Equals(project.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)) return true;
        return subdomains && candidate.IdnHost.EndsWith("." + project.IdnHost, StringComparison.OrdinalIgnoreCase);
    }
}

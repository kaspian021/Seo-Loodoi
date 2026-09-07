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
            if (!candidate.IsAbsoluteUri || candidate.Scheme is not ("http" or "https") || !HostAllowed(projectBaseUri, candidate, includeSubdomains)) continue;
            Uri normalized;
            try { normalized = normalizer.Normalize(candidate); } catch (ArgumentException) { continue; }
            if (await store.EnqueueAsync(new CrawlFrontierItem(crawlId, projectId, candidate.AbsoluteUri, normalized.AbsoluteUri, depth, sourceId), ct)) accepted++;
        }
        return accepted;
    }
    internal static bool HostAllowed(Uri project, Uri candidate, bool subdomains)
    {
        if (string.Equals(project.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)) return true;
        return subdomains && candidate.IdnHost.EndsWith("." + project.IdnHost, StringComparison.OrdinalIgnoreCase);
    }
}

namespace SeoLoodoi.Application.Links;

public sealed record GraphPage(Guid Id, string Url, bool IsRoot = false, bool IsInSitemap = false);
public sealed record GraphLink(Guid SourceId, Guid TargetId, string? AnchorText = null);
public sealed record PageLinkMetrics(Guid PageId, int InDegree, int OutDegree, decimal InternalAuthority, bool IsOrphan, bool IsWeaklyLinked);

public interface IInternalLinkGraph
{
    IReadOnlyList<PageLinkMetrics> Analyze(IReadOnlyList<GraphPage> pages, IReadOnlyList<GraphLink> links, int iterations = 20);
}

public sealed class InternalLinkGraph : IInternalLinkGraph
{
    public IReadOnlyList<PageLinkMetrics> Analyze(IReadOnlyList<GraphPage> pages, IReadOnlyList<GraphLink> links, int iterations = 20)
    {
        if (pages.Count == 0) return [];
        var ids = pages.Select(x => x.Id).ToHashSet();
        var valid = links.Where(x => ids.Contains(x.SourceId) && ids.Contains(x.TargetId)).DistinctBy(x => (x.SourceId, x.TargetId)).ToArray();
        var inbound = valid.GroupBy(x => x.TargetId).ToDictionary(x => x.Key, x => x.Count());
        var outbound = valid.GroupBy(x => x.SourceId).ToDictionary(x => x.Key, x => x.Select(e => e.TargetId).Distinct().ToArray());
        var n = pages.Count; var authority = pages.ToDictionary(x => x.Id, _ => 1m / n); const decimal damping = .85m;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var next = pages.ToDictionary(x => x.Id, _ => (1m - damping) / n);
            var dangling = authority.Where(x => !outbound.TryGetValue(x.Key, out var targets) || targets.Length == 0).Sum(x => x.Value);
            foreach (var id in ids) next[id] += damping * dangling / n;
            foreach (var (source, targets) in outbound)
                foreach (var target in targets) next[target] += damping * authority[source] / targets.Length;
            authority = next;
        }
        var maximum = authority.Values.Max();
        return pages.Select(page =>
        {
            var incoming = inbound.GetValueOrDefault(page.Id);
            var outgoing = outbound.TryGetValue(page.Id, out var targets) ? targets.Length : 0;
            var normalizedAuthority = maximum == 0 ? 0 : decimal.Round(authority[page.Id] / maximum * 100m, 2);
            return new PageLinkMetrics(page.Id, incoming, outgoing, normalizedAuthority, !page.IsRoot && page.IsInSitemap && incoming == 0, !page.IsRoot && incoming <= 1);
        }).ToArray();
    }
}

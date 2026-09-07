using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Content;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Links;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Analysis;

public sealed class AnalyzeCrawlJobHandler(AppDbContext db, IEnumerable<ISeoRule> rules, IScoringEngine scoring, IContentSimilarityEngine similarity, IInternalLinkGraph linkGraph, IUrlNormalizer normalizer) : ISeoJobHandler
{
    public SeoJobType Type => SeoJobType.AnalyzeCrawl;
    public async Task HandleAsync(SeoBackgroundJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CrawlJobPayload>(job.PayloadJson) ?? throw new InvalidDataException("Invalid analysis payload.");
        var crawlExists = await db.Crawls.AnyAsync(x => x.Id == payload.CrawlId && x.ProjectId == payload.ProjectId && x.Status == CrawlStatus.Completed, ct);
        if (!crawlExists) throw new InvalidOperationException("Crawl is not completed.");
        if (db.Database.IsRelational())
        {
            await db.SeoIssues.Where(x => x.CrawlId == payload.CrawlId).ExecuteDeleteAsync(ct);
            await db.SeoScores.Where(x => x.CrawlId == payload.CrawlId).ExecuteDeleteAsync(ct);
        }
        else
        {
            db.SeoIssues.RemoveRange(await db.SeoIssues.Where(x => x.CrawlId == payload.CrawlId).ToListAsync(ct));
            db.SeoScores.RemoveRange(await db.SeoScores.Where(x => x.CrawlId == payload.CrawlId).ToListAsync(ct));
        }
        var pages = await db.CrawledUrls.AsNoTracking().Where(x => x.CrawlId == payload.CrawlId)
            .GroupJoin(db.PageSnapshots.AsNoTracking(), u => u.Id, s => s.CrawledUrlId, (u, snapshots) => new { Url = u, Snapshot = snapshots.FirstOrDefault() }).ToListAsync(ct);
        var allResults = new List<SeoRuleResult>();
        foreach (var page in pages)
        {
            var headings = DeserializeHeadings(page.Snapshot?.HeadingsJson);
            var context = new PageAnalysisContext(page.Url.Url, page.Snapshot?.Title, page.Snapshot?.MetaDescription,
                headings.Where(x => x.Level == 1).Select(x => x.Text).ToArray(), page.Snapshot?.Canonical,
                page.Url.WordCount, page.Snapshot?.ImageCount ?? 0, page.Snapshot?.MissingAltCount ?? 0, page.Url.ResponseTimeMs, page.Url.IsIndexable, headings.Select(x => x.Level).ToArray());
            foreach (var rule in rules)
            {
                var result = rule.Evaluate(context); allResults.Add(result);
                if (!result.Triggered) continue;
                var evidence = JsonSerializer.Serialize(new { page.Url.Url, result.Evidence });
                db.SeoIssues.Add(new SeoIssue(payload.ProjectId, payload.CrawlId, page.Url.Id, result.Code, result.Severity, result.Category, Title(result.Code), Description(result.Code), evidence));
            }
        }
        var contentDocuments = pages.Where(x => !string.IsNullOrWhiteSpace(x.Snapshot?.TextContent)).Select(x => new ContentDocument(x.Url.Id, x.Snapshot!.TextContent)).ToArray();
        foreach (var cluster in similarity.Cluster(contentDocuments, .85m))
        {
            var code = cluster.Exact ? "DUPLICATE_CONTENT" : "NEAR_DUPLICATE_CONTENT";
            var result = new SeoRuleResult(code, true, IssueSeverity.Medium, IssueCategory.Content, new("pageIds", string.Join(",", cluster.DocumentIds), $"Similarity below 0.85; observed {cluster.Similarity}"));
            allResults.Add(result);
            db.SeoIssues.Add(new SeoIssue(payload.ProjectId, payload.CrawlId, null, code, result.Severity, result.Category, Title(code), Description(code), JsonSerializer.Serialize(new { cluster.DocumentIds, cluster.Similarity, cluster.Exact })));
        }

        var normalizedToId = pages.Select(x => (Id:x.Url.Id, Url:NormalizeOrNull(x.Url.Url))).Where(x => x.Url is not null).GroupBy(x => x.Url!, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);
        var seedUrls = (await db.CrawlFrontierItems.AsNoTracking().Where(x => x.CrawlId == payload.CrawlId && x.DiscoveredFromId == null).Select(x => x.NormalizedUrl).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var graphPages = pages.Select(x => { var normalized = NormalizeOrNull(x.Url.Url); var root = Uri.TryCreate(x.Url.Url, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/"; return new GraphPage(x.Url.Id, x.Url.Url, root, normalized is not null && seedUrls.Contains(normalized)); }).ToArray();
        var graphLinks = (await db.PageLinks.AsNoTracking().Where(x => x.CrawlId == payload.CrawlId && x.IsInternal).Select(x => new { x.SourceUrlId, x.NormalizedTarget }).ToListAsync(ct))
            .Where(x => normalizedToId.ContainsKey(x.NormalizedTarget)).Select(x => new GraphLink(x.SourceUrlId, normalizedToId[x.NormalizedTarget])).ToArray();
        foreach (var metric in linkGraph.Analyze(graphPages, graphLinks).Where(x => x.IsOrphan))
        {
            var result = new SeoRuleResult("ORPHAN_PAGE", true, IssueSeverity.High, IssueCategory.InternalLinks, new("inDegree", metric.InDegree.ToString(), "> 0 internal links"));
            allResults.Add(result);
            db.SeoIssues.Add(new SeoIssue(payload.ProjectId, payload.CrawlId, metric.PageId, result.Code, result.Severity, result.Category, Title(result.Code), Description(result.Code), JsonSerializer.Serialize(result.Evidence)));
        }

        var score = scoring.Calculate(allResults);
        db.SeoScores.Add(new SeoScoreSnapshot(payload.ProjectId, payload.CrawlId, score.Overall, score.Categories, score.Version));

        string? NormalizeOrNull(string value) { try { return normalizer.Normalize(new Uri(value)).AbsoluteUri; } catch { return null; } }
        await db.SaveChangesAsync(ct);
    }
    private static IReadOnlyList<ExtractedHeading> DeserializeHeadings(string? json)
    {
        try { return JsonSerializer.Deserialize<ExtractedHeading[]>(json ?? "[]") ?? []; } catch (JsonException) { return []; }
    }
    private static string Title(string code) => code.Replace('_', ' ').ToLowerInvariant();
    private static string Description(string code) => $"Deterministic rule {code} was triggered by the stored crawl evidence. Review the evidence before applying a change.";
}

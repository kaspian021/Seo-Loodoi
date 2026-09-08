using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Competitors;

public sealed record CompetitorCrawlJobPayload(Guid CompetitorCrawlId, Guid ProjectId, Guid CompetitorId);

public interface ICompetitorCrawlRunner { Task RunAsync(SeoBackgroundJob job, CancellationToken ct); }

public sealed class CompetitorCrawlJobHandler(ICompetitorCrawlRunner runner) : ISeoJobHandler
{
    public SeoJobType Type => SeoJobType.CompetitorCrawl;
    public Task HandleAsync(SeoBackgroundJob job, CancellationToken ct) => runner.RunAsync(job, ct);
}

/// <summary>
/// Small, bounded crawl of a competitor host: at most 25 pages, depth at most
/// 1, same host only, SSRF-guarded and redirect-revalidated. Every persisted
/// row is an observed fact; traffic and rank are never estimated.
/// </summary>
public sealed class CompetitorCrawlRunner(AppDbContext db, IOutboundUrlGuard guard, IPageFetcher fetcher, IHtmlExtractor extractor, IRobotsService robotsService, IUrlNormalizer normalizer, ILogger<CompetitorCrawlRunner> logger) : ICompetitorCrawlRunner
{
    private const int MaxResponseBytes = 2_000_000;
    private const int TimeoutSeconds = 20;
    private const string UserAgent = "SEO-LoodoiBot/1.0 (+https://loodoi.example/bot)";

    public async Task RunAsync(SeoBackgroundJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CompetitorCrawlJobPayload>(job.PayloadJson) ?? throw new InvalidDataException("Invalid competitor crawl payload.");
        var competitor = await db.Competitors.SingleOrDefaultAsync(x => x.Id == payload.CompetitorId && x.ProjectId == payload.ProjectId, ct)
            ?? throw new InvalidOperationException("Competitor no longer exists.");
        var crawl = await db.CompetitorCrawls.SingleOrDefaultAsync(x => x.Id == payload.CompetitorCrawlId && x.CompetitorId == competitor.Id, ct)
            ?? throw new InvalidOperationException("Competitor crawl no longer exists.");
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CompetitorCrawlId"] = crawl.Id, ["CompetitorId"] = competitor.Id, ["ProjectId"] = payload.ProjectId });
        if (crawl.Status is CompetitorCrawlStatus.Completed or CompetitorCrawlStatus.Failed or CompetitorCrawlStatus.Cancelled)
        {
            logger.LogInformation("Competitor crawl skipped: terminal state {Status}", crawl.Status);
            return;
        }
        if (crawl.Status == CompetitorCrawlStatus.Queued) crawl.Start(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Competitor crawl started for {Host}", competitor.NormalizedHost);

        var baseUri = new Uri(competitor.BaseUrl);
        var robots = await robotsService.GetPolicyAsync(baseUri, ct);
        var visited = (await db.CompetitorPages.AsNoTracking().Where(x => x.CompetitorCrawlId == crawl.Id).Select(x => x.Url).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Uri Url, int Depth)>();
        if (visited.Count == 0) queue.Enqueue((baseUri, 0));

        while (queue.Count > 0 && crawl.PagesCrawled < CompetitorCrawl.MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            await db.Entry(crawl).ReloadAsync(ct);
            if (crawl.Status != CompetitorCrawlStatus.Running)
            {
                logger.LogInformation("Competitor crawl stopped: status {Status}", crawl.Status);
                return;
            }
            var (uri, depth) = queue.Dequeue();
            string normalized;
            try { normalized = normalizer.Normalize(uri).AbsoluteUri; }
            catch (ArgumentException) { continue; }
            if (!visited.Add(normalized)) continue;
            try
            {
                await guard.ValidateAsync(uri, ct);
                if (!robots.CanCrawl(UserAgent, uri)) { crawl.ReportCrawled(true); await db.SaveChangesAsync(ct); continue; }
                var response = fetcher is IConfigurablePageFetcher configurable
                    ? await configurable.FetchAsync(uri, MaxResponseBytes, UserAgent, true, TimeoutSeconds, ct)
                    : await fetcher.FetchAsync(uri, MaxResponseBytes, ct);
                if (!string.Equals(baseUri.IdnHost, response.FinalUri.IdnHost, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Competitor page skipped: redirect left host {Final}", response.FinalUri.IdnHost);
                    crawl.ReportCrawled(true); await db.SaveChangesAsync(ct); continue;
                }
                await guard.ValidateAsync(response.FinalUri, ct);
                if (!robots.CanCrawl(UserAgent, response.FinalUri)) { crawl.ReportCrawled(true); await db.SaveChangesAsync(ct); continue; }
                ExtractedPage? page = null;
                if (string.Equals(response.ContentType, "text/html", StringComparison.OrdinalIgnoreCase) || response.ContentType?.EndsWith("+html", StringComparison.OrdinalIgnoreCase) == true)
                    page = await extractor.ExtractAsync(Encoding.UTF8.GetString(response.Content), response.FinalUri, ct);
                var isIndexable = response.StatusCode == 200 && !(page?.Robots?.Contains("noindex", StringComparison.OrdinalIgnoreCase) ?? false);
                string? title = null;
                if (page?.Title is { Length: > 0 } pageTitle) title = pageTitle[..Math.Min(pageTitle.Length, 500)];
                db.CompetitorPages.Add(new CompetitorPage(crawl.Id, payload.ProjectId, competitor.Id, response.FinalUri.AbsoluteUri,
                    response.StatusCode, response.ContentType, depth, (long)response.Duration.TotalMilliseconds, isIndexable,
                    page?.WordCount ?? 0, title, !string.IsNullOrWhiteSpace(page?.MetaDescription), page?.Links.Count(x => x.IsInternal) ?? 0));
                if (page is not null && depth < CompetitorCrawl.MaxDepth)
                {
                    foreach (var link in page.Links.Where(x => x.IsInternal))
                    {
                        if (queue.Count + crawl.PagesCrawled >= CompetitorCrawl.MaxPages) break;
                        if (link.Target.Scheme is not ("http" or "https")) continue;
                        if (!string.Equals(baseUri.IdnHost, link.Target.IdnHost, StringComparison.OrdinalIgnoreCase)) continue;
                        queue.Enqueue((link.Target, depth + 1));
                        crawl.ReportDiscovered();
                    }
                }
                crawl.ReportCrawled();
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added && x.Entity is CompetitorPage).ToArray()) entry.State = EntityState.Detached;
                crawl.ReportCrawled(true); await db.SaveChangesAsync(ct);
                logger.LogWarning(ex, "Competitor page persistence failed for {Url}", uri.AbsoluteUri);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UriFormatException)
            {
                foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added && x.Entity is CompetitorPage).ToArray()) entry.State = EntityState.Detached;
                crawl.ReportCrawled(true); await db.SaveChangesAsync(ct);
                logger.LogWarning(ex, "Competitor page fetch failed for {Url}", uri.AbsoluteUri);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        await db.Entry(crawl).ReloadAsync(ct);
        if (crawl.Status != CompetitorCrawlStatus.Running) return;
        var pages = await db.CompetitorPages.AsNoTracking().Where(x => x.CompetitorCrawlId == crawl.Id).ToListAsync(ct);
        var snapshot = new
        {
            pagesCrawled = pages.Count,
            errors = crawl.Errors,
            avgWordCount = pages.Count == 0 ? (double?)null : pages.Average(x => x.WordCount),
            avgResponseMs = pages.Count == 0 ? (double?)null : pages.Average(x => (double)x.ResponseTimeMs),
            indexable = pages.Count(x => x.IsIndexable),
            withTitle = pages.Count(x => !string.IsNullOrWhiteSpace(x.Title)),
            withMetaDescription = pages.Count(x => x.HasMetaDescription),
            finishedAt = DateTimeOffset.UtcNow
        };
        crawl.Complete(JsonSerializer.Serialize(snapshot), DateTimeOffset.UtcNow);
        competitor.MarkCrawled(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Competitor crawl completed: {Pages} pages, {Errors} errors", pages.Count, crawl.Errors);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Crawling;

public interface ICrawlBatchRunner { Task RunAsync(SeoBackgroundJob job, CancellationToken ct); }
public sealed class InitialCrawlJobHandler(ICrawlBatchRunner runner) : ISeoJobHandler { public SeoJobType Type => SeoJobType.InitialCrawl; public Task HandleAsync(SeoBackgroundJob job, CancellationToken ct) => runner.RunAsync(job, ct); }
public sealed class ContinueCrawlJobHandler(ICrawlBatchRunner runner) : ISeoJobHandler { public SeoJobType Type => SeoJobType.ContinueCrawl; public Task HandleAsync(SeoBackgroundJob job, CancellationToken ct) => runner.RunAsync(job, ct); }

public sealed class CrawlBatchRunner(AppDbContext db, ICrawlFrontierStore frontier, ISeoJobQueue jobs, IPageFetcher fetcher, IHtmlExtractor extractor, IRobotsService robotsService, ISitemapDiscoveryService sitemaps, CrawlFrontierPlanner planner, IUrlNormalizer normalizer) : ICrawlBatchRunner
{
    private const int BatchSize = 20;
    public async Task RunAsync(SeoBackgroundJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CrawlJobPayload>(job.PayloadJson) ?? throw new InvalidDataException("Invalid crawl job payload.");
        var project = await db.SeoProjects.SingleAsync(x => x.Id == payload.ProjectId, ct);
        var crawl = await db.Crawls.SingleAsync(x => x.Id == payload.CrawlId && x.ProjectId == project.Id, ct);
        if (crawl.Status is CrawlStatus.Cancelled or CrawlStatus.Completed) return;
        if (crawl.Status is CrawlStatus.Queued or CrawlStatus.Paused) crawl.Start(DateTimeOffset.UtcNow);
        var baseUri = new Uri(project.BaseUrl);
        var robots = await robotsService.GetPolicyAsync(baseUri, ct);
        if (crawl.PagesCrawled == 0)
        {
            var seeds = robots.Document.Sitemaps.Count > 0 ? robots.Document.Sitemaps : [new Uri(baseUri, "/sitemap.xml")];
            var discovered = await sitemaps.DiscoverAsync(seeds, 20, project.Settings.MaxPages, ct);
            var added = await planner.EnqueueDiscoveredAsync(crawl.Id, project.Id, baseUri, discovered.Urls.Select(x => x.Location), 0, project.Settings.MaxDepth, project.Settings.IncludeSubdomains, null, ct);
            crawl.ReportDiscovered(added); await db.SaveChangesAsync(ct);
        }

        var processed = 0;
        while (processed < BatchSize && crawl.PagesCrawled < project.Settings.MaxPages)
        {
            await db.Entry(crawl).ReloadAsync(ct);
            if (crawl.Status is CrawlStatus.Paused or CrawlStatus.Cancelled) return;
            var item = await frontier.TryLeaseAsync(crawl.Id, Environment.MachineName, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), ct);
            if (item is null) break;
            processed++;
            try
            {
                var uri = new Uri(item.Url);
                if (!robots.CanCrawl("SEO-LoodoiBot", uri)) { item.Skip(DateTimeOffset.UtcNow, robots.TemporarilyUnavailable ? "robots.txt temporarily unavailable" : "Blocked by robots.txt"); await frontier.SaveAsync(ct); continue; }
                var response = await fetcher.FetchAsync(uri, project.Settings.MaxResponseBytes, ct);
                ExtractedPage? page = null;
                if (string.Equals(response.ContentType, "text/html", StringComparison.OrdinalIgnoreCase) || response.ContentType?.EndsWith("+html", StringComparison.OrdinalIgnoreCase) == true)
                    page = await extractor.ExtractAsync(Encoding.UTF8.GetString(response.Content), response.FinalUri, ct);
                var hash = Convert.ToHexString(SHA256.HashData(response.Content));
                var isIndexable = response.StatusCode == 200 && !(page?.Robots?.Contains("noindex", StringComparison.OrdinalIgnoreCase) ?? false);
                var crawled = new CrawledUrl(crawl.Id, project.Id, response.FinalUri.AbsoluteUri, page?.Canonical, response.StatusCode, response.ContentType, item.Depth, (long)response.Duration.TotalMilliseconds, isIndexable, page?.WordCount ?? 0, hash);
                db.CrawledUrls.Add(crawled);
                if (page is not null)
                {
                    var retainedText = page.Text[..Math.Min(page.Text.Length, 100_000)];
                    db.PageSnapshots.Add(new PageSnapshot(crawled.Id, crawl.Id, page.Title, page.MetaDescription, page.Headings.FirstOrDefault(x => x.Level == 1)?.Text, JsonSerializer.Serialize(page.Headings), page.Canonical, page.Robots, page.Language, JsonSerializer.Serialize(page.JsonLd), retainedText, page.ImageCount, page.MissingAltCount, page.Links.Count(x => x.IsInternal), page.Links.Count(x => !x.IsInternal)));
                    foreach (var link in page.Links)
                    {
                        Uri normalized; try { normalized = normalizer.Normalize(link.Target); } catch { continue; }
                        db.PageLinks.Add(new PageLink(crawl.Id, crawled.Id, link.Target.AbsoluteUri, normalized.AbsoluteUri, link.AnchorText[..Math.Min(link.AnchorText.Length, 500)], link.Rel, link.IsInternal, link.Rel?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nofollow", StringComparer.OrdinalIgnoreCase) == true));
                    }
                    var added = await planner.EnqueueDiscoveredAsync(crawl.Id, project.Id, baseUri, page.Links.Where(x => x.IsInternal).Select(x => x.Target), item.Depth + 1, project.Settings.MaxDepth, project.Settings.IncludeSubdomains, crawled.Id, ct);
                    crawl.ReportDiscovered(added);
                }
                item.Complete(DateTimeOffset.UtcNow); crawl.ReportCrawled(); crawl.Heartbeat(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                item.Retry(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, item.Attempts))), ex.GetType().Name, project.Settings.RetryCount + 1);
                crawl.ReportCrawled(true); await db.SaveChangesAsync(ct);
            }
            if (project.Settings.DelayMilliseconds > 0) await Task.Delay(project.Settings.DelayMilliseconds, ct);
        }

        var hasWork = await db.CrawlFrontierItems.AnyAsync(x => x.CrawlId == crawl.Id && (x.Status == FrontierStatus.Pending || x.Status == FrontierStatus.Leased), ct);
        if (hasWork && crawl.PagesCrawled < project.Settings.MaxPages)
            await jobs.EnqueueOnceAsync(SeoJobType.ContinueCrawl, $"continue-crawl:{crawl.Id}:{crawl.PagesCrawled}", JsonSerializer.Serialize(payload), ct);
        else if (crawl.Status == CrawlStatus.Running)
        {
            crawl.Complete(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
            await jobs.EnqueueOnceAsync(SeoJobType.AnalyzeCrawl, $"analyze-crawl:{crawl.Id}", JsonSerializer.Serialize(payload), ct);
        }
    }
}

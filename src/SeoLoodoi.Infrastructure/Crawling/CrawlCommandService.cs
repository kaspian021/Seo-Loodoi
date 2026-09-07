using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class CrawlCommandService(AppDbContext db, ICrawlFrontierStore frontier, ISeoJobQueue jobs, IUrlNormalizer normalizer) : ICrawlCommandService
{
    public async Task<Crawl?> StartAsync(Guid projectId, Guid ownerId, CancellationToken ct)
    {
        var project = await db.SeoProjects.SingleOrDefaultAsync(x => x.Id == projectId && x.OwnerId == ownerId && x.Status == ProjectStatus.Active, ct);
        if (project is null) return null;
        if (await db.Crawls.AnyAsync(x => x.ProjectId == projectId && (x.Status == CrawlStatus.Queued || x.Status == CrawlStatus.Running || x.Status == CrawlStatus.Paused), ct)) throw new InvalidOperationException("An active crawl already exists.");
        var crawl = new Crawl(projectId, CrawlTrigger.Manual); db.Crawls.Add(crawl); await db.SaveChangesAsync(ct);
        var baseUri = new Uri(project.BaseUrl); var normalized = normalizer.Normalize(baseUri);
        await frontier.EnqueueAsync(new CrawlFrontierItem(crawl.Id, project.Id, baseUri.AbsoluteUri, normalized.AbsoluteUri, 0), ct);
        await jobs.EnqueueOnceAsync(SeoJobType.InitialCrawl, $"initial-crawl:{crawl.Id}", JsonSerializer.Serialize(new CrawlJobPayload(crawl.Id, project.Id)), ct);
        crawl.ReportDiscovered(); await db.SaveChangesAsync(ct); return crawl;
    }
    public Task<bool> PauseAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct) => ChangeAsync(projectId, crawlId, ownerId, (c,n) => c.Pause(n), ct);
    public async Task<bool> ResumeAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct)
    {
        var changed = await ChangeAsync(projectId, crawlId, ownerId, (c,n) => c.Start(n), ct);
        if (changed) await jobs.EnqueueOnceAsync(SeoJobType.ContinueCrawl, $"continue-crawl:{crawlId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", JsonSerializer.Serialize(new CrawlJobPayload(crawlId, projectId)), ct);
        return changed;
    }
    public Task<bool> CancelAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct) => ChangeAsync(projectId, crawlId, ownerId, (c,n) => c.Cancel(n), ct);
    private async Task<bool> ChangeAsync(Guid projectId, Guid crawlId, Guid ownerId, Action<Crawl,DateTimeOffset> action, CancellationToken ct)
    {
        var crawl = await db.Crawls.Where(c => c.Id == crawlId && c.ProjectId == projectId).Join(db.SeoProjects.Where(p => p.OwnerId == ownerId), c => c.ProjectId, p => p.Id, (c,_) => c).SingleOrDefaultAsync(ct);
        if (crawl is null) return false; action(crawl, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); return true;
    }
}

public sealed record CrawlJobPayload(Guid CrawlId, Guid ProjectId);

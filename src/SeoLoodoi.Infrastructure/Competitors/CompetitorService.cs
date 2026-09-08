using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Competitors;

public sealed class CompetitorService(AppDbContext db, IProjectAccessService access, IQuotaService quota, IOutboundUrlGuard guard, IAuditLogService audit, ISeoJobQueue jobs) : ICompetitorService
{
    public async Task<IReadOnlyList<CompetitorDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        return await db.Competitors.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Name)
            .Select(x => new CompetitorDto(x.Id, x.Name, x.BaseUrl, x.NormalizedHost, x.IsActive, x.LastCrawlAt, x.CreatedAt)).ToListAsync(ct);
    }

    public async Task<CompetitorDto?> CreateAsync(Guid projectId, Guid userId, CreateCompetitorRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;
        if (!Uri.TryCreate(request.BaseUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("A valid absolute HTTP(S) competitor URL is required.");
        await guard.ValidateAsync(uri, ct);
        await quota.EnsureCanAddCompetitorAsync(projectId, userId, ct);
        var competitor = new Competitor(projectId, request.Name, uri); db.Competitors.Add(competitor); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "COMPETITOR_CREATED", "Competitor", competitor.Id.ToString(), "{}", null, ct);
        return new CompetitorDto(competitor.Id, competitor.Name, competitor.BaseUrl, competitor.NormalizedHost, competitor.IsActive, competitor.LastCrawlAt, competitor.CreatedAt);
    }

    public async Task<bool> UpdateAsync(Guid projectId, Guid competitorId, Guid userId, UpdateCompetitorRequest request, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return false;
        var competitor = await db.Competitors.SingleOrDefaultAsync(x => x.Id == competitorId && x.ProjectId == projectId, ct); if (competitor is null) return false;
        competitor.SetActive(request.IsActive); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "COMPETITOR_UPDATED", "Competitor", competitorId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { request.IsActive }), null, ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanManageAsync(projectId, userId, ct)) return false;
        var competitor = await db.Competitors.SingleOrDefaultAsync(x => x.Id == competitorId && x.ProjectId == projectId, ct); if (competitor is null) return false;
        db.Competitors.Remove(competitor); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "COMPETITOR_DELETED", "Competitor", competitorId.ToString(), "{}", null, ct);
        return true;
    }

    public async Task<CompetitorCrawlDto?> StartCrawlAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;
        var competitor = await db.Competitors.SingleOrDefaultAsync(x => x.Id == competitorId && x.ProjectId == projectId, ct);
        if (competitor is null || !competitor.IsActive) return null;
        if (await db.CompetitorCrawls.AnyAsync(x => x.CompetitorId == competitorId && (x.Status == CompetitorCrawlStatus.Queued || x.Status == CompetitorCrawlStatus.Running), ct))
            throw new InvalidOperationException("A competitor crawl is already running.");
        var crawl = new CompetitorCrawl(projectId, competitorId);
        db.CompetitorCrawls.Add(crawl); await db.SaveChangesAsync(ct);
        await jobs.EnqueueOnceAsync(SeoJobType.CompetitorCrawl, $"competitor-crawl:{crawl.Id}", JsonSerializer.Serialize(new CompetitorCrawlJobPayload(crawl.Id, projectId, competitorId)), ct);
        await audit.RecordAsync(projectId, userId, "COMPETITOR_CRAWL_STARTED", "CompetitorCrawl", crawl.Id.ToString(), JsonSerializer.Serialize(new { competitorId }), null, ct);
        return ToDto(crawl);
    }

    public async Task<IReadOnlyList<CompetitorCrawlDto>> ListCrawlsAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        if (!await db.Competitors.AnyAsync(x => x.Id == competitorId && x.ProjectId == projectId, ct)) return [];
        return await db.CompetitorCrawls.AsNoTracking().Where(x => x.CompetitorId == competitorId && x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt).Take(20)
            .Select(x => new CompetitorCrawlDto(x.Id, x.CompetitorId, x.Status.ToString(), x.PagesDiscovered, x.PagesCrawled, x.Errors, x.StartedAt, x.FinishedAt, x.LastError, x.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<CompetitorCrawlDto?> LatestCrawlAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        return await db.CompetitorCrawls.AsNoTracking().Where(x => x.CompetitorId == competitorId && x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new CompetitorCrawlDto(x.Id, x.CompetitorId, x.Status.ToString(), x.PagesDiscovered, x.PagesCrawled, x.Errors, x.StartedAt, x.FinishedAt, x.LastError, x.CreatedAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CompetitorComparisonDto?> CompareAsync(Guid projectId, Guid userId, Guid? crawlId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        var projectCrawl = crawlId is null
            ? await db.Crawls.AsNoTracking().Where(x => x.ProjectId == projectId && x.Status == CrawlStatus.Completed).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct)
            : await db.Crawls.AsNoTracking().SingleOrDefaultAsync(x => x.Id == crawlId && x.ProjectId == projectId && x.Status == CrawlStatus.Completed, ct);
        CompetitorMetricsDto? projectMetrics = null;
        if (projectCrawl is not null)
        {
            var pages = await db.CrawledUrls.AsNoTracking().Where(x => x.CrawlId == projectCrawl.Id).ToListAsync(ct);
            var snapshots = await db.PageSnapshots.AsNoTracking().Where(x => x.CrawlId == projectCrawl.Id).ToListAsync(ct);
            projectMetrics = Metrics(pages.Count, pages.Select(x => (double)x.WordCount).ToArray(), pages.Select(x => (double)x.ResponseTimeMs).ToArray(),
                pages.Count(x => x.IsIndexable), snapshots.Count(x => !string.IsNullOrWhiteSpace(x.Title)), snapshots.Count(x => !string.IsNullOrWhiteSpace(x.MetaDescription)),
                snapshots.Sum(x => x.InternalLinkCount));
        }
        var competitors = await db.Competitors.AsNoTracking().Where(x => x.ProjectId == projectId && x.IsActive).OrderBy(x => x.Name).ToListAsync(ct);
        var entries = new List<CompetitorComparisonEntryDto>();
        foreach (var competitor in competitors)
        {
            var latest = await db.CompetitorCrawls.AsNoTracking().Where(x => x.CompetitorId == competitor.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            CompetitorMetricsDto? metrics = null;
            if (latest?.Status == CompetitorCrawlStatus.Completed)
            {
                var pages = await db.CompetitorPages.AsNoTracking().Where(x => x.CompetitorCrawlId == latest.Id).ToListAsync(ct);
                metrics = Metrics(pages.Count, pages.Select(x => (double)x.WordCount).ToArray(), pages.Select(x => (double)x.ResponseTimeMs).ToArray(),
                    pages.Count(x => x.IsIndexable), pages.Count(x => !string.IsNullOrWhiteSpace(x.Title)), pages.Count(x => x.HasMetaDescription),
                    pages.Sum(x => x.InternalLinkCount));
            }
            entries.Add(new CompetitorComparisonEntryDto(competitor.Id, competitor.Name, competitor.BaseUrl,
                latest is null ? null : new CompetitorCrawlDto(latest.Id, latest.CompetitorId, latest.Status.ToString(), latest.PagesDiscovered, latest.PagesCrawled, latest.Errors, latest.StartedAt, latest.FinishedAt, latest.LastError, latest.CreatedAt),
                metrics));
        }
        return new CompetitorComparisonDto(projectId, projectCrawl?.Id, projectMetrics, entries);
    }

    private static CompetitorMetricsDto Metrics(int pages, double[] words, double[] responseMs, int indexable, int withTitle, int withMeta, int internalLinks) =>
        new(pages,
            pages == 0 ? null : words.Average(),
            pages == 0 ? null : responseMs.Average(),
            pages == 0 ? null : (double)indexable / pages,
            pages == 0 ? null : (double)withTitle / pages,
            pages == 0 ? null : (double)withMeta / pages,
            internalLinks);

    private static CompetitorCrawlDto ToDto(CompetitorCrawl crawl) =>
        new(crawl.Id, crawl.CompetitorId, crawl.Status.ToString(), crawl.PagesDiscovered, crawl.PagesCrawled, crawl.Errors, crawl.StartedAt, crawl.FinishedAt, crawl.LastError, crawl.CreatedAt);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Projects;

public sealed class QuotaOptions
{
    public string DefaultPlan { get; set; } = "Starter";
    public int MaxProjects { get; set; } = 3;
    public int PagesPerMonth { get; set; } = 500;
    public int MaxKeywords { get; set; } = 25;
    public int MaxCompetitors { get; set; } = 3;
}

public sealed class QuotaService(AppDbContext db, IOptions<QuotaOptions> options) : IQuotaService
{
    private readonly QuotaOptions _limits = options.Value;

    public async Task<QuotaStatus> GetAsync(Guid userId, CancellationToken ct)
    {
        var accountOwnerId = await FindBillingOwnerAsync(userId, ct);
        var periodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var periodStartUtc = new DateTimeOffset(periodStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var projectsUsed = await db.SeoProjects.CountAsync(x => x.OwnerId == accountOwnerId && x.Status != Domain.Seo.ProjectStatus.Archived, ct);
        var pagesUsed = await db.CrawledUrls.CountAsync(x => x.ProjectId != Guid.Empty && db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == accountOwnerId) && db.Crawls.Any(c => c.Id == x.CrawlId && c.StartedAt >= periodStartUtc), ct);
        var keywordsUsed = await db.Keywords.CountAsync(x => db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == accountOwnerId), ct);
        var competitorsUsed = await db.Competitors.CountAsync(x => db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == accountOwnerId), ct);
        return new QuotaStatus(_limits.DefaultPlan, _limits.MaxProjects, projectsUsed, _limits.PagesPerMonth, pagesUsed, _limits.MaxKeywords, keywordsUsed, _limits.MaxCompetitors, competitorsUsed, periodStart);
    }

    private async Task<Guid> FindBillingOwnerAsync(Guid userId, CancellationToken ct)
    {
        var owned = await db.SeoProjects.Where(x => x.OwnerId == userId).Select(x => (Guid?)x.OwnerId).FirstOrDefaultAsync(ct);
        if (owned is not null) return owned.Value;
        return await db.ProjectMembers.Where(x => x.UserId == userId).Join(db.SeoProjects, member => member.ProjectId, project => project.Id, (_, project) => (Guid?)project.OwnerId).FirstOrDefaultAsync(ct) ?? userId;
    }

    public async Task EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct)
    {
        var used = await db.SeoProjects.CountAsync(x => x.OwnerId == userId && x.Status != Domain.Seo.ProjectStatus.Archived, ct);
        if (used >= _limits.MaxProjects) throw new QuotaExceededException($"Project quota for {_limits.DefaultPlan} plan has been reached ({_limits.MaxProjects}).");
    }

    public async Task EnsureCanStartCrawlAsync(Guid projectId, Guid userId, int requestedPages, CancellationToken ct)
    {
        var projectOwnerId = await db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct);
        if (projectOwnerId is null) return;
        var periodStart = new DateTimeOffset(new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var used = await db.CrawledUrls.CountAsync(x => x.ProjectId != Guid.Empty && db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == projectOwnerId) && db.Crawls.Any(c => c.Id == x.CrawlId && c.StartedAt >= periodStart), ct);
        if (used + Math.Max(1, requestedPages) > _limits.PagesPerMonth) throw new QuotaExceededException($"Monthly crawl quota for {_limits.DefaultPlan} plan has been reached ({_limits.PagesPerMonth} pages).");
    }

    public async Task EnsureCanAddKeywordAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var ownerId = await db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct); if (ownerId is null) return;
        var used = await db.Keywords.CountAsync(x => db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == ownerId), ct);
        if (used >= _limits.MaxKeywords) throw new QuotaExceededException($"Keyword quota for {_limits.DefaultPlan} plan has been reached ({_limits.MaxKeywords}).");
    }

    public async Task EnsureCanAddCompetitorAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var ownerId = await db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct); if (ownerId is null) return;
        var used = await db.Competitors.CountAsync(x => db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == ownerId), ct);
        if (used >= _limits.MaxCompetitors) throw new QuotaExceededException($"Competitor quota for {_limits.DefaultPlan} plan has been reached ({_limits.MaxCompetitors}).");
    }
}

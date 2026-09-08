using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Analysis;

public sealed class AuditQueryService(AppDbContext db, IProjectAccessService access) : IAuditQueryService
{
    public async Task<IReadOnlyList<IssueDto>> ListIssuesAsync(Guid projectId, Guid ownerId, Guid? crawlId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, ownerId, ct)) return [];
        var query = db.SeoIssues.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (crawlId is not null) query = query.Where(x => x.CrawlId == crawlId);
        return await query.OrderByDescending(x => x.Severity).ThenBy(x => x.RuleCode)
            .Select(x => new IssueDto(x.Id, x.UrlId, x.RuleCode, x.Severity.ToString(), x.Category.ToString(), x.Title, x.Description, x.EvidenceJson, x.Status.ToString(), x.UrlId == null ? null : db.CrawledUrls.Where(u => u.Id == x.UrlId).Select(u => u.Url).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<ScoreDto?> LatestScoreAsync(Guid projectId, Guid ownerId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, ownerId, ct)) return null;
        return await db.SeoScores.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new ScoreDto(x.OverallScore, x.TechnicalScore, x.IndexabilityScore, x.OnPageScore, x.ContentScore, x.LinksScore, x.StructuredDataScore, x.PerformanceScore, x.InternationalScore, x.SecurityScore, x.CalculationVersion, x.CreatedAt, x.CrawlId, x.IsPartial)).FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ScoreDto>> ScoreHistoryAsync(Guid projectId, Guid ownerId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, ownerId, ct)) return [];
        return await db.SeoScores.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new ScoreDto(x.OverallScore, x.TechnicalScore, x.IndexabilityScore, x.OnPageScore, x.ContentScore, x.LinksScore, x.StructuredDataScore, x.PerformanceScore, x.InternationalScore, x.SecurityScore, x.CalculationVersion, x.CreatedAt, x.CrawlId, x.IsPartial))
            .ToListAsync(ct);
    }

    public async Task<DashboardDto?> DashboardAsync(Guid projectId, Guid ownerId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, ownerId, ct)) return null;
        var project = await db.SeoProjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (project is null) return null;
        var crawl = await db.Crawls.AsNoTracking().Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var issueQuery = db.SeoIssues.AsNoTracking().Where(x => x.ProjectId == projectId && x.Status == Domain.Seo.IssueStatus.Open);
        if (crawl is not null) issueQuery = issueQuery.Where(x => x.CrawlId == crawl.Id);
        var openIssues = await issueQuery.CountAsync(ct);
        var critical = await issueQuery.CountAsync(x => x.Severity == Domain.Seo.IssueSeverity.Critical || x.Severity == Domain.Seo.IssueSeverity.High, ct);
        var score = await LatestScoreAsync(projectId, ownerId, ct);
        // Never present a previous crawl's score as the current crawl's result.
        if (score is not null && crawl is not null && score.CrawlId != crawl.Id) score = null;
        var top = await ListIssuesAsync(projectId, ownerId, crawl?.Id, ct);
        return new DashboardDto(project.Id, project.Name, project.BaseUrl, crawl?.Status.ToString(), crawl?.PagesDiscovered ?? 0, crawl?.PagesCrawled ?? 0, crawl?.Errors ?? 0, openIssues, critical, score, top.Take(8).ToArray(), crawl?.FinishedAt ?? crawl?.StartedAt);
    }
}

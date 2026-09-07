using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Analysis;

public sealed class AuditQueryService(AppDbContext db) : IAuditQueryService
{
    public async Task<IReadOnlyList<IssueDto>> ListIssuesAsync(Guid projectId, Guid ownerId, Guid? crawlId, CancellationToken ct)
    {
        var query = db.SeoIssues.AsNoTracking().Where(x => x.ProjectId == projectId && db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == ownerId));
        if (crawlId is not null) query = query.Where(x => x.CrawlId == crawlId);
        return await query.OrderByDescending(x => x.Severity).ThenBy(x => x.RuleCode).Select(x => new IssueDto(x.Id, x.UrlId, x.RuleCode, x.Severity.ToString(), x.Category.ToString(), x.Title, x.EvidenceJson)).ToListAsync(ct);
    }
    public Task<ScoreDto?> LatestScoreAsync(Guid projectId, Guid ownerId, CancellationToken ct) => db.SeoScores.AsNoTracking()
        .Where(x => x.ProjectId == projectId && db.SeoProjects.Any(p => p.Id == x.ProjectId && p.OwnerId == ownerId)).OrderByDescending(x => x.CreatedAt)
        .Select(x => new ScoreDto(x.OverallScore, x.TechnicalScore, x.IndexabilityScore, x.OnPageScore, x.ContentScore, x.LinksScore, x.StructuredDataScore, x.PerformanceScore, x.InternationalScore, x.SecurityScore, x.CalculationVersion, x.CreatedAt)).FirstOrDefaultAsync(ct);
}

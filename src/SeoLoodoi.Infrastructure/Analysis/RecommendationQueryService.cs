using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Analysis;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Analysis;

public sealed class RecommendationQueryService(AppDbContext db, IProjectAccessService access, IAuditLogService audit) : IRecommendationQueryService
{
    public async Task<IReadOnlyList<RecommendationDto>> ListAsync(Guid projectId, Guid userId, RecommendationStatus? status, CancellationToken ct, Guid? crawlId = null)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        var query = db.Recommendations.AsNoTracking().Where(x => x.ProjectId == projectId);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (crawlId is not null)
        {
            // Recommendations are rebuilt per crawl; scope them to the current
            // crawl through their linked issue so stale tasks never surface.
            var issueIds = db.SeoIssues.AsNoTracking().Where(x => x.ProjectId == projectId && x.CrawlId == crawlId).Select(x => x.Id);
            query = query.Where(x => x.IssueId != null && issueIds.Contains(x.IssueId.Value));
        }
        return await query.OrderByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt)
            .Select(x => new RecommendationDto(x.Id, x.IssueId, x.Priority, x.Title, x.Explanation, x.EvidenceJson, x.ExpectedImpact, x.Effort, x.Confidence, x.Status.ToString(), x.CreatedAt)).ToListAsync(ct);
    }

    public async Task<bool> UpdateStatusAsync(Guid projectId, Guid recommendationId, Guid userId, RecommendationStatus status, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return false;
        var item = await db.Recommendations.SingleOrDefaultAsync(x => x.Id == recommendationId && x.ProjectId == projectId, ct);
        if (item is null) return false;
        item.ChangeStatus(status); await db.SaveChangesAsync(ct);
        await audit.RecordAsync(projectId, userId, "RECOMMENDATION_STATUS_CHANGED", "Recommendation", recommendationId.ToString(), System.Text.Json.JsonSerializer.Serialize(new { status }), null, ct);
        return true;
    }
}

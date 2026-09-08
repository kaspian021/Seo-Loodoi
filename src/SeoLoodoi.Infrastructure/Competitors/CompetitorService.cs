using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Competitors;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Infrastructure.Competitors;

public sealed class CompetitorService(AppDbContext db, IProjectAccessService access, IQuotaService quota, IOutboundUrlGuard guard, IAuditLogService audit) : ICompetitorService
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
}

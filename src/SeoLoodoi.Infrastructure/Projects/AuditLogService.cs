using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Projects;

public sealed class AuditLogService(AppDbContext db, IProjectAccessService access) : IAuditLogService
{
    public async Task RecordAsync(Guid? projectId, Guid actorId, string action, string entityType, string? entityId, string? metadataJson, string? ipAddress, CancellationToken ct)
    {
        if (projectId is not null && !await access.CanViewAsync(projectId.Value, actorId, ct)) return;
        db.AuditLogs.Add(new AuditLog(projectId, actorId, action, entityType, entityId, metadataJson, ipAddress));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanManageAsync(projectId, userId, ct)) return [];
        return await db.AuditLogs.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new AuditLogDto(x.Id, x.ProjectId, x.ActorId, x.Action, x.EntityType, x.EntityId, x.MetadataJson, x.IpAddress, x.CreatedAt))
            .ToListAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Projects;

public sealed class ProjectAccessService(AppDbContext db) : IProjectAccessService
{
    public async Task<ProjectAccess?> GetAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var owner = await db.SeoProjects.AsNoTracking().AnyAsync(x => x.Id == projectId && x.OwnerId == userId, ct);
        if (owner) return new ProjectAccess(projectId, userId, true, ProjectMemberRole.Admin);
        return await db.ProjectMembers.AsNoTracking().Where(x => x.ProjectId == projectId && x.UserId == userId)
            .Select(x => new ProjectAccess(projectId, userId, false, x.Role)).SingleOrDefaultAsync(ct);
    }

    public async Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) => await GetAsync(projectId, userId, ct) is not null;
    public async Task<bool> CanEditAsync(Guid projectId, Guid userId, CancellationToken ct) => (await GetAsync(projectId, userId, ct))?.CanEdit == true;
    public async Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) => (await GetAsync(projectId, userId, ct))?.CanManage == true;
}

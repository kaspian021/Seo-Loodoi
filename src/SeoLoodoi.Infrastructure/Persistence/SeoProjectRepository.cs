using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Infrastructure.Persistence;

internal sealed class SeoProjectRepository(AppDbContext db) : ISeoProjectRepository
{
    public async Task<IReadOnlyList<SeoProject>> ListForOwnerAsync(Guid ownerId, CancellationToken ct) => await db.SeoProjects.AsNoTracking()
        .Where(x => x.OwnerId == ownerId || db.ProjectMembers.Any(m => m.ProjectId == x.Id && m.UserId == ownerId))
        .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
    public Task<SeoProject?> FindOwnedAsync(Guid projectId, Guid ownerId, CancellationToken ct) => db.SeoProjects.SingleOrDefaultAsync(x => x.Id == projectId && (x.OwnerId == ownerId || db.ProjectMembers.Any(m => m.ProjectId == x.Id && m.UserId == ownerId)), ct);
    public Task AddAsync(SeoProject project, CancellationToken ct) => db.SeoProjects.AddAsync(project, ct).AsTask();
    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

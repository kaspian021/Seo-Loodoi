using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Projects;

public interface ISeoProjectRepository
{
    Task<IReadOnlyList<SeoProject>> ListForOwnerAsync(Guid ownerId, CancellationToken ct);
    Task<SeoProject?> FindOwnedAsync(Guid projectId, Guid ownerId, CancellationToken ct);
    Task AddAsync(SeoProject project, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record CreateProjectRequest(string Name, string BaseUrl);

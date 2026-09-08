using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Projects;

public interface ISeoProjectRepository
{
    /// <summary>Lists projects the user owns or has been added to.</summary>
    Task<IReadOnlyList<SeoProject>> ListForOwnerAsync(Guid ownerId, CancellationToken ct);
    /// <summary>Returns a project only when the user has access to it.</summary>
    Task<SeoProject?> FindOwnedAsync(Guid projectId, Guid ownerId, CancellationToken ct);
    Task AddAsync(SeoProject project, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record CreateProjectRequest(string Name, string BaseUrl);

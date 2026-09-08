namespace SeoLoodoi.Application.Competitors;

public sealed record CompetitorDto(Guid Id, string Name, string BaseUrl, string NormalizedHost, bool IsActive, DateTimeOffset? LastCrawlAt, DateTimeOffset CreatedAt);
public sealed record CreateCompetitorRequest(string Name, string BaseUrl);
public sealed record UpdateCompetitorRequest(bool IsActive);
public interface ICompetitorService
{
    Task<IReadOnlyList<CompetitorDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<CompetitorDto?> CreateAsync(Guid projectId, Guid userId, CreateCompetitorRequest request, CancellationToken ct);
    Task<bool> UpdateAsync(Guid projectId, Guid competitorId, Guid userId, UpdateCompetitorRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct);
}

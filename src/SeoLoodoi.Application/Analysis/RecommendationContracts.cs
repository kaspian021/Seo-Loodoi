using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Analysis;

public sealed record RecommendationDto(Guid Id, Guid? IssueId, int Priority, string Title, string Explanation, string EvidenceJson, string ExpectedImpact, string Effort, decimal Confidence, string Status, DateTimeOffset CreatedAt);
public sealed record UpdateRecommendationStatusRequest(RecommendationStatus Status);

public interface IRecommendationQueryService
{
    Task<IReadOnlyList<RecommendationDto>> ListAsync(Guid projectId, Guid userId, RecommendationStatus? status, CancellationToken ct, Guid? crawlId = null);
    Task<bool> UpdateStatusAsync(Guid projectId, Guid recommendationId, Guid userId, RecommendationStatus status, CancellationToken ct);
}

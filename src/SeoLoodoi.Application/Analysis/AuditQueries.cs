namespace SeoLoodoi.Application.Analysis;

public sealed record IssueDto(Guid Id, Guid? UrlId, string RuleCode, string Severity, string Category, string Title, string EvidenceJson);
public sealed record ScoreDto(decimal Overall, decimal Technical, decimal Indexability, decimal OnPage, decimal Content, decimal Links, decimal StructuredData, decimal Performance, decimal International, decimal Security, string Version, DateTimeOffset CreatedAt);
public interface IAuditQueryService
{
    Task<IReadOnlyList<IssueDto>> ListIssuesAsync(Guid projectId, Guid ownerId, Guid? crawlId, CancellationToken ct);
    Task<ScoreDto?> LatestScoreAsync(Guid projectId, Guid ownerId, CancellationToken ct);
}

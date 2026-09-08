namespace SeoLoodoi.Application.Analysis;

public sealed record IssueDto(Guid Id, Guid? UrlId, string RuleCode, string Severity, string Category, string Title, string Description, string EvidenceJson, string Status, string? Url)
{
    public IssueDto(Guid id, Guid? urlId, string ruleCode, string severity, string category, string title, string evidenceJson)
        : this(id, urlId, ruleCode, severity, category, title, string.Empty, evidenceJson, "Open", null) { }
}
public sealed record ScoreDto(decimal Overall, decimal Technical, decimal Indexability, decimal OnPage, decimal Content, decimal Links, decimal StructuredData, decimal Performance, decimal International, decimal Security, string Version, DateTimeOffset CreatedAt);
public sealed record DashboardDto(Guid ProjectId, string ProjectName, string BaseUrl, string? LatestCrawlStatus, int PagesDiscovered, int PagesCrawled, int CrawlErrors, int OpenIssues, int CriticalIssues, ScoreDto? Score, IReadOnlyList<IssueDto> TopIssues, DateTimeOffset? LastCrawlAt);
public interface IAuditQueryService
{
    Task<IReadOnlyList<IssueDto>> ListIssuesAsync(Guid projectId, Guid ownerId, Guid? crawlId, CancellationToken ct);
    Task<ScoreDto?> LatestScoreAsync(Guid projectId, Guid ownerId, CancellationToken ct);
    Task<IReadOnlyList<ScoreDto>> ScoreHistoryAsync(Guid projectId, Guid ownerId, CancellationToken ct);
    Task<DashboardDto?> DashboardAsync(Guid projectId, Guid ownerId, CancellationToken ct);
}

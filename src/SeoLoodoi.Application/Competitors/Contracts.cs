namespace SeoLoodoi.Application.Competitors;

public sealed record CompetitorDto(Guid Id, string Name, string BaseUrl, string NormalizedHost, bool IsActive, DateTimeOffset? LastCrawlAt, DateTimeOffset CreatedAt);
public sealed record CreateCompetitorRequest(string Name, string BaseUrl);
public sealed record UpdateCompetitorRequest(bool IsActive);
public sealed record CompetitorCrawlDto(Guid Id, Guid CompetitorId, string Status, int PagesDiscovered, int PagesCrawled, int Errors, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? LastError, DateTimeOffset CreatedAt);
public sealed record CompetitorMetricsDto(int PagesCrawled, double? AvgWordCount, double? AvgResponseMs, double? IndexableRate, double? TitleCoverage, double? MetaCoverage, int InternalLinks);
public sealed record CompetitorComparisonEntryDto(Guid CompetitorId, string Name, string BaseUrl, CompetitorCrawlDto? LatestCrawl, CompetitorMetricsDto? Metrics);
public sealed record CompetitorComparisonDto(Guid ProjectId, Guid? ProjectCrawlId, CompetitorMetricsDto? ProjectMetrics, IReadOnlyList<CompetitorComparisonEntryDto> Competitors);
public interface ICompetitorService
{
    Task<IReadOnlyList<CompetitorDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<CompetitorDto?> CreateAsync(Guid projectId, Guid userId, CreateCompetitorRequest request, CancellationToken ct);
    Task<bool> UpdateAsync(Guid projectId, Guid competitorId, Guid userId, UpdateCompetitorRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct);
    Task<CompetitorCrawlDto?> StartCrawlAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<CompetitorCrawlDto>> ListCrawlsAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct);
    Task<CompetitorCrawlDto?> LatestCrawlAsync(Guid projectId, Guid competitorId, Guid userId, CancellationToken ct);
    Task<CompetitorComparisonDto?> CompareAsync(Guid projectId, Guid userId, Guid? crawlId, CancellationToken ct);
}

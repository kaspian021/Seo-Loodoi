using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Keywords;

public sealed record KeywordHistoryPointDto(DateOnly Date, decimal Position, int Impressions, int Clicks);

public sealed record KeywordDto(
    Guid Id,
    string Phrase,
    string Language,
    string Country,
    bool IsTracked,
    DateTimeOffset? LastMetricAt,
    int? Clicks,
    int? Impressions,
    decimal? Ctr,
    decimal? AveragePosition,
    string? BestPage,
    string Source,
    decimal? CurrentPosition = null,
    decimal? PreviousPosition = null,
    decimal? PositionDelta = null,
    string Trend = "stable",
    IReadOnlyList<KeywordHistoryPointDto>? History = null);

public sealed record CreateKeywordRequest(string Phrase, string Language = "fa", string Country = "IR", bool IsTracked = true);
public sealed record BatchCreateKeywordsRequest(IReadOnlyList<string> Phrases, string Language = "fa", string Country = "IR");
public sealed record BatchCreateKeywordsResult(int AddedCount, int SkippedCount, IReadOnlyList<KeywordDto> AddedKeywords);

public sealed record CompetingPageDto(string PageUrl, int Impressions, int Clicks, decimal AveragePosition, decimal ImpressionSharePercentage);

public sealed record KeywordCannibalizationDto(
    Guid KeywordId,
    string Phrase,
    IReadOnlyList<CompetingPageDto> CompetingPages,
    string Severity,
    string Explanation);

public sealed record KeywordRankingSummaryDto(
    int TotalTracked,
    int Top3Count,
    int Top10Count,
    int Top20Count,
    int Top100Count,
    int ImprovedCount,
    int DeclinedCount,
    int CannibalizationCount,
    int TotalImpressions,
    int TotalClicks);

public sealed record ImportKeywordMetricRequest(DateOnly Date, int Clicks, int Impressions, decimal Ctr, decimal AveragePosition, string Source, string? PageUrl = null, string Country = "IR", string Device = "ALL");
public sealed record KeywordOpportunityDto(Guid KeywordId, string Phrase, int Impressions, int Clicks, decimal Ctr, decimal AveragePosition, string? PageUrl, int OpportunityScore, string Reason);

public interface IKeywordService
{
    Task<IReadOnlyList<KeywordDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<KeywordDto?> CreateAsync(Guid projectId, Guid userId, CreateKeywordRequest request, CancellationToken ct);
    Task<BatchCreateKeywordsResult> BatchCreateAsync(Guid projectId, Guid userId, BatchCreateKeywordsRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid projectId, Guid keywordId, Guid userId, CancellationToken ct);
    Task<bool> AddMetricAsync(Guid projectId, Guid keywordId, Guid userId, ImportKeywordMetricRequest request, CancellationToken ct);
    Task<IReadOnlyList<KeywordOpportunityDto>> OpportunitiesAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<KeywordCannibalizationDto>> CannibalizationAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<KeywordRankingSummaryDto> SummaryAsync(Guid projectId, Guid userId, CancellationToken ct);
}

using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Keywords;

public sealed record KeywordDto(Guid Id, string Phrase, string Language, string Country, bool IsTracked, DateTimeOffset? LastMetricAt, int? Clicks, int? Impressions, decimal? Ctr, decimal? AveragePosition, string? BestPage, string Source);
public sealed record CreateKeywordRequest(string Phrase, string Language = "fa", string Country = "IR", bool IsTracked = true);
public sealed record ImportKeywordMetricRequest(DateOnly Date, int Clicks, int Impressions, decimal Ctr, decimal AveragePosition, string Source, string? PageUrl = null, string Country = "IR", string Device = "ALL");
public sealed record KeywordOpportunityDto(Guid KeywordId, string Phrase, int Impressions, int Clicks, decimal Ctr, decimal AveragePosition, string? PageUrl, int OpportunityScore, string Reason);
public interface IKeywordService
{
    Task<IReadOnlyList<KeywordDto>> ListAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<KeywordDto?> CreateAsync(Guid projectId, Guid userId, CreateKeywordRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid projectId, Guid keywordId, Guid userId, CancellationToken ct);
    Task<bool> AddMetricAsync(Guid projectId, Guid keywordId, Guid userId, ImportKeywordMetricRequest request, CancellationToken ct);
    Task<IReadOnlyList<KeywordOpportunityDto>> OpportunitiesAsync(Guid projectId, Guid userId, CancellationToken ct);
}

using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Aeo;

public sealed record AiCrawlerProfileDto(
    Guid Id,
    string Key,
    string DisplayName,
    string UserAgentToken,
    AiCrawlerPurpose Purpose,
    decimal Weight,
    bool IsEnabled,
    string? Notes);

/// <summary>What robots.txt says about one crawler. Unspecified means no group matched.</summary>
public sealed record AiCrawlerAccessDto(
    string Key,
    string DisplayName,
    string UserAgentToken,
    AiCrawlerPurpose Purpose,
    string Access,
    decimal Weight);

/// <summary>
/// The observable signals behind the answer/citation scores. Every field is a
/// count taken from stored crawl evidence - nothing here is estimated.
/// </summary>
public sealed record AeoSignalsDto(
    int PagesAnalyzed,
    int PagesWithQuestionHeadings,
    int PagesWithFaqSchema,
    int PagesWithAnySchema,
    int PagesWithEntitySchema,
    int PagesWithAuthorOrDate,
    int PagesWithCanonical,
    int PagesWithConciseAnswer,
    int PagesWithHeadingStructure);

public sealed record AiVisibilityReportDto(
    Guid ProjectId,
    Guid CrawlId,
    decimal? AiCrawlabilityScore,
    decimal? AnswerReadinessScore,
    decimal? CitationReadinessScore,
    decimal? AiVisibilityScore,
    IReadOnlyList<AiCrawlerAccessDto> CrawlerAccess,
    AeoSignalsDto Signals,
    IReadOnlyList<string> Findings,
    bool RobotsAvailable);

/// <summary>The per-page evidence the analyzer is allowed to use.</summary>
public sealed record AeoPageInput(
    string Url,
    string? TextContent,
    string? SchemaJson,
    string? HeadingsJson,
    string? Canonical,
    int WordCount);

public interface IAeoAnalyzer
{
    /// <summary>
    /// Deterministic AEO/GEO assessment. Returns null scores when the crawl
    /// produced no usable evidence - an unknown score is never reported as zero.
    /// </summary>
    AiVisibilityReportDto Analyze(
        Guid projectId,
        Guid crawlId,
        string? robotsTxt,
        Uri siteOrigin,
        IReadOnlyList<AiCrawlerProfileDto> profiles,
        IReadOnlyList<AeoPageInput> pages);
}

public interface IAeoService
{
    Task<IReadOnlyList<AiCrawlerProfileDto>> ListProfilesAsync(CancellationToken ct);

    Task<AiVisibilityReportDto?> AnalyzeAsync(Guid projectId, Guid crawlId, Guid userId, CancellationToken ct);

    Task<AiVisibilityReportDto?> LatestAsync(Guid projectId, Guid crawlId, Guid userId, CancellationToken ct);
}

namespace SeoLoodoi.Application.AI;

public sealed record AiCrawlOverview(int TotalPages, int IndexablePages, int ErrorPages, long AvgResponseMs);
public sealed record AiLinkGraphOverview(int OrphanPages, int DeadEndPages, int TotalInternalLinks);
public sealed record AiContentOverview(int ThinContentPages, int KeywordStuffingPages, decimal AvgReadabilityScore);
public sealed record AiKeywordOverview(int TrackedKeywords, int Top10Keywords, int CannibalizationAlerts);
public sealed record AiCompetitorOverview(int ActiveCompetitors, int MissingTopicsCount);

public sealed record AiIssueEvidence(string Code, string Severity, string Category, string EvidenceJson);

public sealed record AiEvidencePacket(
    Guid ProjectId,
    string? Url,
    int? StatusCode,
    string? Title,
    string? H1,
    int? WordCount,
    IReadOnlyList<AiIssueEvidence> Issues,
    AiCrawlOverview? CrawlOverview = null,
    AiLinkGraphOverview? LinkGraphOverview = null,
    AiContentOverview? ContentOverview = null,
    AiKeywordOverview? KeywordOverview = null,
    AiCompetitorOverview? CompetitorOverview = null);

public sealed record AiSeoResponse(
    string Summary,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> RootCauses,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> Actions,
    decimal Confidence,
    IReadOnlyList<string> MissingEvidence,
    string Provider,
    string PromptVersion);

public interface IAiSeoExpert
{
    Task<AiSeoResponse> AnalyzeAsync(AiEvidencePacket packet, CancellationToken ct);
}

public interface IAiAnalysisService
{
    Task<AiSeoResponse?> AnalyzeProjectAsync(Guid projectId, Guid userId, Guid? crawlId, CancellationToken ct);
}

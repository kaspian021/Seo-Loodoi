namespace SeoLoodoi.Application.AI;

public sealed record AiEvidencePacket(Guid ProjectId, string? Url, int? StatusCode, string? Title, string? H1, int? WordCount, IReadOnlyList<AiIssueEvidence> Issues);
public sealed record AiIssueEvidence(string Code, string Severity, string Category, string EvidenceJson);
public sealed record AiSeoResponse(string Summary, IReadOnlyList<string> Observations, IReadOnlyList<string> RootCauses, IReadOnlyList<string> Recommendations, IReadOnlyList<string> Actions, decimal Confidence, IReadOnlyList<string> MissingEvidence, string Provider, string PromptVersion);
public interface IAiSeoExpert
{
    Task<AiSeoResponse> AnalyzeAsync(AiEvidencePacket packet, CancellationToken ct);
}
public interface IAiAnalysisService
{
    Task<AiSeoResponse?> AnalyzeProjectAsync(Guid projectId, Guid userId, Guid? crawlId, CancellationToken ct);
}

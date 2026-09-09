using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

public sealed class SeoIssue : Entity
{
    private SeoIssue() { }
    public SeoIssue(Guid projectId, Guid crawlId, Guid? urlId, string ruleCode, IssueSeverity severity, IssueCategory category, string title, string description, string evidenceJson)
    { ProjectId = projectId; CrawlId = crawlId; UrlId = urlId; RuleCode = ruleCode; Severity = severity; Category = category; Title = title; Description = description; EvidenceJson = evidenceJson; }
    public Guid ProjectId { get; private set; }
    public Guid CrawlId { get; private set; }
    public Guid? UrlId { get; private set; }
    public string RuleCode { get; private set; } = string.Empty;
    public IssueSeverity Severity { get; private set; }
    public IssueCategory Category { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string EvidenceJson { get; private set; } = "{}";
    public IssueStatus Status { get; private set; } = IssueStatus.Open;
    public void ChangeStatus(IssueStatus status) { Status = status; UpdatedAt = DateTimeOffset.UtcNow; }
}

public sealed class SeoScoreSnapshot : Entity
{
    private SeoScoreSnapshot() { }
    public SeoScoreSnapshot(Guid projectId, Guid crawlId, decimal? overall, IReadOnlyDictionary<IssueCategory, decimal?> scores, string version, bool isPartial)
    {
        ProjectId = projectId; CrawlId = crawlId; OverallScore = overall; CalculationVersion = version; IsPartial = isPartial;
        TechnicalScore = Get(IssueCategory.Technical); IndexabilityScore = Get(IssueCategory.Indexability); OnPageScore = Get(IssueCategory.OnPage);
        ContentScore = Get(IssueCategory.Content); LinksScore = Get(IssueCategory.InternalLinks); StructuredDataScore = Get(IssueCategory.StructuredData);
        PerformanceScore = Get(IssueCategory.Performance); InternationalScore = Get(IssueCategory.International); SecurityScore = Get(IssueCategory.Security);
        // Categories without evaluated evidence stay null. A missing score must
        // never be rendered as a fabricated 100.
        decimal? Get(IssueCategory category) => scores.TryGetValue(category, out var value) ? value : null;
    }
    public Guid ProjectId { get; private set; }
    public Guid CrawlId { get; private set; }
    public decimal? OverallScore { get; private set; }
    public decimal? TechnicalScore { get; private set; }
    public decimal? IndexabilityScore { get; private set; }
    public decimal? OnPageScore { get; private set; }
    public decimal? ContentScore { get; private set; }
    public decimal? LinksScore { get; private set; }
    public decimal? StructuredDataScore { get; private set; }
    public decimal? PerformanceScore { get; private set; }
    public decimal? InternationalScore { get; private set; }
    public decimal? SecurityScore { get; private set; }
    public bool IsPartial { get; private set; }
    public string CalculationVersion { get; private set; } = "2.0.0";
}

/// <summary>
/// Durable lifecycle of the deterministic analysis for one crawl. The crawl can
/// be Completed while its analysis is still Pending, Running, Failed or NoData;
/// the API and UI must always read this row instead of inferring state.
/// </summary>
public enum AnalysisStatus { Pending, Running, Succeeded, Failed, NoData }

public sealed class CrawlAnalysis : Entity
{
    private CrawlAnalysis() { }
    public CrawlAnalysis(Guid crawlId, Guid projectId, string jobKey)
    {
        if (crawlId == Guid.Empty || projectId == Guid.Empty) throw new ArgumentException("Crawl and project are required.");
        if (string.IsNullOrWhiteSpace(jobKey)) throw new ArgumentException("Analysis job key is required.", nameof(jobKey));
        CrawlId = crawlId; ProjectId = projectId; LastJobKey = jobKey.Trim();
    }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public AnalysisStatus Status { get; private set; } = AnalysisStatus.Pending;
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public string LastJobKey { get; private set; } = string.Empty;
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    public void MarkRunning(DateTimeOffset now, string jobKey)
    {
        if (Status == AnalysisStatus.Running) return;
        Status = AnalysisStatus.Running; Attempts++; StartedAt ??= now;
        LastJobKey = jobKey; LastError = null; UpdatedAt = now;
    }
    public void RecordAttemptFailure(string error, DateTimeOffset now)
    {
        Attempts++; LastError = error[..Math.Min(error.Length, 2000)]; UpdatedAt = now;
    }
    public void MarkSucceeded(DateTimeOffset now) { Status = AnalysisStatus.Succeeded; FinishedAt = now; LastError = null; UpdatedAt = now; }
    public void MarkNoData(DateTimeOffset now) { Status = AnalysisStatus.NoData; FinishedAt = now; LastError = null; UpdatedAt = now; }
    public void MarkFailed(string error, DateTimeOffset now) { Status = AnalysisStatus.Failed; FinishedAt = now; LastError = error[..Math.Min(error.Length, 2000)]; UpdatedAt = now; }
    public void ResetForRetry(string jobKey, DateTimeOffset now)
    {
        if (Status is not (AnalysisStatus.Failed or AnalysisStatus.NoData or AnalysisStatus.Pending)) throw new InvalidOperationException($"Analysis in state {Status} cannot be retried.");
        Status = AnalysisStatus.Pending; LastJobKey = jobKey; LastError = null; FinishedAt = null; UpdatedAt = now;
    }
}

public sealed class Recommendation : Entity
{
    private Recommendation() { }
    public Recommendation(Guid projectId, Guid? issueId, int priority, string title, string explanation, string evidenceJson, string expectedImpact, string effort, decimal confidence)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        ProjectId = projectId; IssueId = issueId; Priority = Math.Clamp(priority, 0, 100); Title = title.Trim(); Explanation = explanation.Trim(); EvidenceJson = evidenceJson; ExpectedImpact = expectedImpact.Trim(); Effort = effort.Trim(); Confidence = Math.Clamp(confidence, 0m, 1m); Status = RecommendationStatus.Proposed;
    }
    public Guid ProjectId { get; private set; }
    public Guid? IssueId { get; private set; }
    public int Priority { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Explanation { get; private set; } = string.Empty;
    public string EvidenceJson { get; private set; } = "{}";
    public string ExpectedImpact { get; private set; } = string.Empty;
    public string Effort { get; private set; } = string.Empty;
    public decimal Confidence { get; private set; }
    public RecommendationStatus Status { get; private set; }
    public void ChangeStatus(RecommendationStatus status) { Status = status; UpdatedAt = DateTimeOffset.UtcNow; }
}

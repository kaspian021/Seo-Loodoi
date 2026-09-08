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
    public SeoScoreSnapshot(Guid projectId, Guid crawlId, decimal overall, IReadOnlyDictionary<IssueCategory, decimal> scores, string version)
    {
        ProjectId = projectId; CrawlId = crawlId; OverallScore = overall; CalculationVersion = version;
        TechnicalScore = Get(IssueCategory.Technical); IndexabilityScore = Get(IssueCategory.Indexability); OnPageScore = Get(IssueCategory.OnPage);
        ContentScore = Get(IssueCategory.Content); LinksScore = Get(IssueCategory.InternalLinks); StructuredDataScore = Get(IssueCategory.StructuredData);
        PerformanceScore = Get(IssueCategory.Performance); InternationalScore = Get(IssueCategory.International); SecurityScore = Get(IssueCategory.Security);
        decimal Get(IssueCategory category) => scores.TryGetValue(category, out var value) ? value : 100m;
    }
    public Guid ProjectId { get; private set; }
    public Guid CrawlId { get; private set; }
    public decimal OverallScore { get; private set; }
    public decimal TechnicalScore { get; private set; }
    public decimal IndexabilityScore { get; private set; }
    public decimal OnPageScore { get; private set; }
    public decimal ContentScore { get; private set; }
    public decimal LinksScore { get; private set; }
    public decimal StructuredDataScore { get; private set; }
    public decimal PerformanceScore { get; private set; }
    public decimal InternationalScore { get; private set; }
    public decimal SecurityScore { get; private set; }
    public string CalculationVersion { get; private set; } = "1.0.0";
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

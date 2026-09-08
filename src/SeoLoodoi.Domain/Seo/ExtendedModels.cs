using System.Text.Json;
using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>Manually entered or provider-backed search query tracked for a project.</summary>
public sealed class Keyword : Entity
{
    private Keyword() { }

    public Keyword(Guid projectId, string phrase, string language = "fa", string country = "IR")
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(phrase) || phrase.Trim().Length > 200) throw new ArgumentException("A keyword phrase between 1 and 200 characters is required.", nameof(phrase));
        ProjectId = projectId;
        Phrase = phrase.Trim();
        NormalizedPhrase = Normalize(Phrase);
        Language = string.IsNullOrWhiteSpace(language) ? "fa" : language.Trim().ToLowerInvariant()[..Math.Min(10, language.Trim().Length)];
        Country = string.IsNullOrWhiteSpace(country) ? "IR" : country.Trim().ToUpperInvariant()[..Math.Min(10, country.Trim().Length)];
    }

    public Guid ProjectId { get; private set; }
    public string Phrase { get; private set; } = string.Empty;
    public string NormalizedPhrase { get; private set; } = string.Empty;
    public string Language { get; private set; } = "fa";
    public string Country { get; private set; } = "IR";
    public bool IsTracked { get; private set; }
    public DateTimeOffset? LastMetricAt { get; private set; }

    public void SetTracking(bool tracked) { IsTracked = tracked; UpdatedAt = DateTimeOffset.UtcNow; }
    public void TouchMetrics(DateTimeOffset at) { LastMetricAt = at; UpdatedAt = at; }

    public static string Normalize(string phrase) => phrase.Trim().Normalize(System.Text.NormalizationForm.FormKC)
        .Replace('ي', 'ی').Replace('ى', 'ی').Replace('ك', 'ک').ToLowerInvariant();
}

/// <summary>A single, source-labelled search performance observation. No estimates are stored as facts.</summary>
public sealed class KeywordMetric : Entity
{
    private KeywordMetric() { }

    public KeywordMetric(Guid projectId, Guid keywordId, DateOnly date, int clicks, int impressions, decimal ctr, decimal averagePosition, string source, string? pageUrl = null, string country = "IR", string device = "ALL")
    {
        if (projectId == Guid.Empty || keywordId == Guid.Empty) throw new ArgumentException("Project and keyword are required.");
        if (clicks < 0 || impressions < 0) throw new ArgumentOutOfRangeException(nameof(clicks));
        if (ctr is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ctr));
        if (averagePosition < 0) throw new ArgumentOutOfRangeException(nameof(averagePosition));
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Metric source is required.", nameof(source));
        ProjectId = projectId; KeywordId = keywordId; Date = date; Clicks = clicks; Impressions = impressions;
        Ctr = decimal.Round(ctr, 6); AveragePosition = decimal.Round(averagePosition, 2);
        Source = source.Trim()[..Math.Min(40, source.Trim().Length)]; PageUrl = string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl.Trim()[..Math.Min(2048, pageUrl.Trim().Length)];
        Country = string.IsNullOrWhiteSpace(country) ? "ALL" : country.Trim().ToUpperInvariant()[..Math.Min(10, country.Trim().Length)];
        Device = string.IsNullOrWhiteSpace(device) ? "ALL" : device.Trim().ToUpperInvariant()[..Math.Min(20, device.Trim().Length)];
    }

    public Guid ProjectId { get; private set; }
    public Guid KeywordId { get; private set; }
    public DateOnly Date { get; private set; }
    public int Clicks { get; private set; }
    public int Impressions { get; private set; }
    public decimal Ctr { get; private set; }
    public decimal AveragePosition { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string? PageUrl { get; private set; }
    public string Country { get; private set; } = "ALL";
    public string Device { get; private set; } = "ALL";
}

public sealed class Competitor : Entity
{
    private Competitor() { }

    public Competitor(Guid projectId, string name, Uri baseUri)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160) throw new ArgumentException("Competitor name is required.", nameof(name));
        if (baseUri.Scheme is not ("http" or "https")) throw new ArgumentException("Only HTTP(S) websites are supported.", nameof(baseUri));
        ProjectId = projectId; Name = name.Trim(); BaseUrl = baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'); NormalizedHost = baseUri.IdnHost.ToLowerInvariant(); IsActive = true;
    }

    public Guid ProjectId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;
    public string NormalizedHost { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTimeOffset? LastCrawlAt { get; private set; }
    public void SetActive(bool active) { IsActive = active; UpdatedAt = DateTimeOffset.UtcNow; }
    public void MarkCrawled(DateTimeOffset at) { LastCrawlAt = at; UpdatedAt = at; }
}

public enum CompetitorCrawlStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>
/// A small, controlled crawl of a competitor host. Bounded to 25 pages and
/// depth 1 so a competitor can never consume the project's crawl budget.
/// </summary>
public sealed class CompetitorCrawl : Entity
{
    public const int MaxPages = 25;
    public const int MaxDepth = 1;

    private CompetitorCrawl() { }

    public CompetitorCrawl(Guid projectId, Guid competitorId)
    {
        if (projectId == Guid.Empty || competitorId == Guid.Empty) throw new ArgumentException("Project and competitor are required.");
        ProjectId = projectId; CompetitorId = competitorId;
    }

    public Guid ProjectId { get; private set; }
    public Guid CompetitorId { get; private set; }
    public CompetitorCrawlStatus Status { get; private set; } = CompetitorCrawlStatus.Queued;
    public int PagesDiscovered { get; private set; }
    public int PagesCrawled { get; private set; }
    public int Errors { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? LastError { get; private set; }
    public string EvidenceSnapshotJson { get; private set; } = "{}";

    public void Start(DateTimeOffset now)
    {
        if (Status != CompetitorCrawlStatus.Queued) throw new InvalidOperationException($"Cannot start competitor crawl from {Status}.");
        Status = CompetitorCrawlStatus.Running; StartedAt ??= now; UpdatedAt = now;
    }
    public void Cancel(DateTimeOffset now) { if (Status is CompetitorCrawlStatus.Completed or CompetitorCrawlStatus.Cancelled) return; Status = CompetitorCrawlStatus.Cancelled; FinishedAt = now; UpdatedAt = now; }
    public void Complete(string evidenceSnapshotJson, DateTimeOffset now)
    {
        if (Status != CompetitorCrawlStatus.Running) throw new InvalidOperationException("Only a running competitor crawl can complete.");
        Status = CompetitorCrawlStatus.Completed; EvidenceSnapshotJson = evidenceSnapshotJson; FinishedAt = now; UpdatedAt = now;
    }
    public void Fail(string error, DateTimeOffset now) { Status = CompetitorCrawlStatus.Failed; LastError = error[..Math.Min(error.Length, 2000)]; FinishedAt = now; UpdatedAt = now; }
    public void ReportDiscovered(int count = 1) { if (count < 0) throw new ArgumentOutOfRangeException(nameof(count)); PagesDiscovered += count; }
    public void ReportCrawled(bool failed = false) { PagesCrawled++; if (failed) Errors++; }
}

/// <summary>
/// One real competitor page. Only observed facts are stored; traffic, rank and
/// authority are never estimated here.
/// </summary>
public sealed class CompetitorPage : Entity
{
    private CompetitorPage() { }

    public CompetitorPage(Guid competitorCrawlId, Guid projectId, Guid competitorId, string url, int statusCode, string? contentType, int depth, long responseTimeMs, bool isIndexable, int wordCount, string? title, bool hasMetaDescription, int internalLinkCount)
    {
        CompetitorCrawlId = competitorCrawlId; ProjectId = projectId; CompetitorId = competitorId;
        Url = url; StatusCode = statusCode; ContentType = contentType; Depth = depth;
        ResponseTimeMs = responseTimeMs; IsIndexable = isIndexable; WordCount = wordCount;
        Title = title; HasMetaDescription = hasMetaDescription; InternalLinkCount = internalLinkCount;
    }

    public Guid CompetitorCrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid CompetitorId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public int StatusCode { get; private set; }
    public string? ContentType { get; private set; }
    public int Depth { get; private set; }
    public long ResponseTimeMs { get; private set; }
    public bool IsIndexable { get; private set; }
    public int WordCount { get; private set; }
    public string? Title { get; private set; }
    public bool HasMetaDescription { get; private set; }
    public int InternalLinkCount { get; private set; }
}

public sealed class ExternalConnection : Entity
{
    private ExternalConnection() { }
    public ExternalConnection(Guid projectId, ExternalProvider provider, string encryptedAccessToken, string? encryptedRefreshToken, DateTimeOffset? expiresAt)
    {
        ProjectId = projectId; Provider = provider; EncryptedAccessToken = encryptedAccessToken; EncryptedRefreshToken = encryptedRefreshToken; ExpiresAt = expiresAt; Status = "Connected";
    }
    public Guid ProjectId { get; private set; }
    public ExternalProvider Provider { get; private set; }
    public string EncryptedAccessToken { get; private set; } = string.Empty;
    public string? EncryptedRefreshToken { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public string Status { get; private set; } = "Disconnected";
    public void UpdateTokens(string accessToken, string? refreshToken, DateTimeOffset? expiresAt) { EncryptedAccessToken = accessToken; EncryptedRefreshToken = refreshToken ?? EncryptedRefreshToken; ExpiresAt = expiresAt; Status = "Connected"; UpdatedAt = DateTimeOffset.UtcNow; }
    public void Disconnect() { Status = "Disconnected"; UpdatedAt = DateTimeOffset.UtcNow; }
}

public sealed class AiAnalysis : Entity
{
    private AiAnalysis() { }
    public AiAnalysis(Guid projectId, string type, string inputEvidenceHash, string promptVersion, string outputJson, decimal confidence)
    {
        ProjectId = projectId; Type = type; InputEvidenceHash = inputEvidenceHash; PromptVersion = promptVersion; OutputJson = outputJson; Confidence = Math.Clamp(confidence, 0m, 1m);
    }
    public Guid ProjectId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string InputEvidenceHash { get; private set; } = string.Empty;
    public string PromptVersion { get; private set; } = string.Empty;
    public string OutputJson { get; private set; } = "{}";
    public decimal Confidence { get; private set; }
}

public sealed class SeoReport : Entity
{
    private SeoReport() { }
    public SeoReport(Guid projectId, string type, string format, Guid? crawlId, string content)
    {
        ProjectId = projectId; Type = type; Format = format; CrawlId = crawlId; Content = content; Status = "Ready";
    }
    public Guid ProjectId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Format { get; private set; } = "json";
    public Guid? CrawlId { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Queued";
    public string? FilePath { get; private set; }
    public void MarkFailed(string reason) { Status = "Failed"; Content = reason[..Math.Min(2000, reason.Length)]; UpdatedAt = DateTimeOffset.UtcNow; }
}

public sealed class AlertRule : Entity
{
    private AlertRule() { }
    public AlertRule(Guid projectId, string type, decimal threshold, string channel = "dashboard", string? destination = null)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("Alert type is required.", nameof(type));
        if (threshold is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(threshold));
        ProjectId = projectId; Type = type.Trim().ToUpperInvariant(); Threshold = threshold; Channel = channel.Trim().ToLowerInvariant(); Destination = destination?.Trim(); IsEnabled = true;
    }
    public Guid ProjectId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public decimal Threshold { get; private set; }
    public string Channel { get; private set; } = "dashboard";
    public string? Destination { get; private set; }
    public bool IsEnabled { get; private set; }
    public void Configure(bool enabled, decimal threshold, string channel, string? destination) { if (threshold is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(threshold)); IsEnabled = enabled; Threshold = threshold; Channel = channel.Trim().ToLowerInvariant(); Destination = destination?.Trim(); UpdatedAt = DateTimeOffset.UtcNow; }
}

public sealed class AlertEvent : Entity
{
    private AlertEvent() { }
    public AlertEvent(Guid projectId, Guid alertRuleId, string eventType, string payloadJson, DateTimeOffset? detectedAt = null)
    {
        ProjectId = projectId; AlertRuleId = alertRuleId; EventType = eventType; PayloadJson = payloadJson; DetectedAt = detectedAt ?? DateTimeOffset.UtcNow;
    }
    public Guid ProjectId { get; private set; }
    public Guid AlertRuleId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public DateTimeOffset DetectedAt { get; private set; }
    public bool IsRead { get; private set; }
    public void MarkRead() { IsRead = true; UpdatedAt = DateTimeOffset.UtcNow; }
}

public sealed class ProjectMember : Entity
{
    private ProjectMember() { }
    public ProjectMember(Guid projectId, Guid userId, ProjectMemberRole role)
    {
        if (projectId == Guid.Empty || userId == Guid.Empty) throw new ArgumentException("Project and user are required.");
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ProjectId = projectId; UserId = userId; Role = role;
    }
    public Guid ProjectId { get; private set; }
    public Guid UserId { get; private set; }
    public ProjectMemberRole Role { get; private set; }
    public void ChangeRole(ProjectMemberRole role) { if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role)); Role = role; UpdatedAt = DateTimeOffset.UtcNow; }
}

public enum ProjectMemberRole { Viewer, Editor, Admin }

public static class MetricJson
{
    public static string Safe(object value) => JsonSerializer.Serialize(value);
}

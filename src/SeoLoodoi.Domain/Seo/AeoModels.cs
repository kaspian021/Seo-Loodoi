using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>Why an AI crawler fetches a page. Different purposes carry different weight.</summary>
public enum AiCrawlerPurpose
{
    /// <summary>Retrieval that answers a user query directly (ChatGPT search, Perplexity).</summary>
    AnswerEngine = 0,

    /// <summary>General AI-assisted search indexing.</summary>
    AiSearch = 1,

    /// <summary>Model training crawlers; visibility here does not produce citations.</summary>
    Training = 2
}

/// <summary>
/// A configurable definition of an AI crawler.
/// <para>
/// Deliberately persisted rather than hardcoded: the set of AI crawlers changes
/// faster than this codebase does, and an operator must be able to add, retire
/// or re-weight one without a release.
/// </para>
/// </summary>
public sealed class AiCrawlerProfile : Entity
{
    private AiCrawlerProfile() { }

    public AiCrawlerProfile(string key, string displayName, string userAgentToken, AiCrawlerPurpose purpose, decimal weight, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(userAgentToken)) throw new ArgumentException("User-agent token is required.", nameof(userAgentToken));

        Key = key.Trim().ToLowerInvariant();
        DisplayName = displayName.Trim();
        UserAgentToken = userAgentToken.Trim();
        Purpose = purpose;
        Weight = Math.Clamp(weight, 0m, 1m);
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        IsEnabled = true;
    }

    public string Key { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>The user-agent token matched against robots.txt, e.g. <c>GPTBot</c>.</summary>
    public string UserAgentToken { get; private set; } = string.Empty;

    public AiCrawlerPurpose Purpose { get; private set; }

    /// <summary>Relative influence on the crawlability score, 0..1.</summary>
    public decimal Weight { get; private set; }

    public bool IsEnabled { get; private set; }
    public string? Notes { get; private set; }

    public void SetEnabled(bool enabled) { IsEnabled = enabled; UpdatedAt = DateTimeOffset.UtcNow; }
    public void SetWeight(decimal weight) { Weight = Math.Clamp(weight, 0m, 1m); UpdatedAt = DateTimeOffset.UtcNow; }
}

/// <summary>
/// A deterministic AEO/GEO assessment of one crawl. Scores are nullable: when the
/// crawl produced no usable evidence the score is unknown, never zero.
/// </summary>
public sealed class AiVisibilitySnapshot : Entity
{
    private AiVisibilitySnapshot() { }

    public AiVisibilitySnapshot(Guid projectId, Guid crawlId)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (crawlId == Guid.Empty) throw new ArgumentException("Crawl is required.", nameof(crawlId));
        ProjectId = projectId;
        CrawlId = crawlId;
    }

    public Guid ProjectId { get; private set; }
    public Guid CrawlId { get; private set; }

    public decimal? AiCrawlabilityScore { get; private set; }
    public decimal? AnswerReadinessScore { get; private set; }
    public decimal? CitationReadinessScore { get; private set; }
    public decimal? AiVisibilityScore { get; private set; }

    public int CrawlersAllowed { get; private set; }
    public int CrawlersBlocked { get; private set; }
    public int CrawlersUnspecified { get; private set; }
    public int PagesAnalyzed { get; private set; }

    /// <summary>Per-crawler decisions and the observable signals behind each score.</summary>
    public string EvidenceJson { get; private set; } = "{}";

    public DateTimeOffset? ComputedAt { get; private set; }

    public void Apply(
        decimal? crawlability,
        decimal? answerReadiness,
        decimal? citationReadiness,
        decimal? visibility,
        int allowed,
        int blocked,
        int unspecified,
        int pagesAnalyzed,
        string evidenceJson,
        DateTimeOffset computedAt)
    {
        AiCrawlabilityScore = crawlability;
        AnswerReadinessScore = answerReadiness;
        CitationReadinessScore = citationReadiness;
        AiVisibilityScore = visibility;
        CrawlersAllowed = allowed;
        CrawlersBlocked = blocked;
        CrawlersUnspecified = unspecified;
        PagesAnalyzed = pagesAnalyzed;
        EvidenceJson = string.IsNullOrWhiteSpace(evidenceJson) ? "{}" : evidenceJson;
        ComputedAt = computedAt;
        UpdatedAt = computedAt;
    }
}

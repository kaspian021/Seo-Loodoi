using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>
/// Same provenance states as backlinks: "no provider" and "provider failed" are
/// never collapsed into an empty result set, because an empty SERP would read as
/// "this keyword ranks nowhere" rather than "we could not look".
/// </summary>
public enum SerpAvailability
{
    NotConfigured = 0,
    Unavailable = 1,
    Partial = 2,
    Available = 3
}

public enum SerpDevice { Desktop = 0, Mobile = 1 }

/// <summary>Which result surface was captured: the classic organic list or an AI answer surface.</summary>
public enum SerpSurface { Organic = 0, AiSearch = 1 }

public enum SerpFeatureKind
{
    FeaturedSnippet = 0,
    PeopleAlsoAsk = 1,
    LocalPack = 2,
    ImagePack = 3,
    VideoResults = 4,
    ShoppingResults = 5,
    NewsResults = 6,
    KnowledgePanel = 7,
    AiOverview = 8,
    TopStories = 9,
    Sitelinks = 10,
    Other = 99
}

/// <summary>
/// One capture of a search result page for a tracked keyword. Snapshots are
/// appended, never rewritten, so rank history stays reconstructable.
/// </summary>
public sealed class SerpSnapshot : Entity
{
    private SerpSnapshot() { }

    public SerpSnapshot(
        Guid projectId,
        Guid? keywordId,
        string phrase,
        string normalizedPhrase,
        string country,
        string language,
        SerpDevice device,
        SerpSurface surface,
        string providerName)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(phrase)) throw new ArgumentException("Search phrase is required.", nameof(phrase));
        if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentException("Provider name is required.", nameof(providerName));

        ProjectId = projectId;
        KeywordId = keywordId;
        Phrase = phrase.Trim()[..Math.Min(200, phrase.Trim().Length)];
        NormalizedPhrase = normalizedPhrase.Trim()[..Math.Min(200, normalizedPhrase.Trim().Length)];
        Country = country.Trim().ToUpperInvariant()[..Math.Min(10, country.Trim().Length)];
        Language = language.Trim().ToLowerInvariant()[..Math.Min(10, language.Trim().Length)];
        Device = device;
        Surface = surface;
        ProviderName = providerName.Trim();
        Availability = SerpAvailability.NotConfigured;
        FeaturesJson = "[]";
    }

    public Guid ProjectId { get; private set; }

    /// <summary>Null once a keyword is deleted, so history survives the keyword row.</summary>
    public Guid? KeywordId { get; private set; }

    public string Phrase { get; private set; } = string.Empty;
    public string NormalizedPhrase { get; private set; } = string.Empty;
    public string Country { get; private set; } = "IR";
    public string Language { get; private set; } = "fa";
    public SerpDevice Device { get; private set; }
    public SerpSurface Surface { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public SerpAvailability Availability { get; private set; }
    public string? Note { get; private set; }

    public int ResultCount { get; private set; }
    public bool ResultsTruncated { get; private set; }

    /// <summary>Position of the tracked site in these results, or null when it was not observed.</summary>
    public int? OwnPosition { get; private set; }
    public string? OwnUrl { get; private set; }

    public string FeaturesJson { get; private set; } = "[]";
    public string? ProviderPayloadHash { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }

    public bool HasData => Availability is SerpAvailability.Available or SerpAvailability.Partial;

    public void RecordSuccess(
        SerpAvailability availability,
        int resultCount,
        bool resultsTruncated,
        string featuresJson,
        int? ownPosition,
        string? ownUrl,
        string? providerPayloadHash,
        DateTimeOffset at)
    {
        if (availability is not (SerpAvailability.Available or SerpAvailability.Partial))
            throw new ArgumentException("A successful snapshot must be Available or Partial.", nameof(availability));

        Availability = availability;
        Note = null;
        ResultCount = Math.Max(0, resultCount);
        ResultsTruncated = resultsTruncated;
        FeaturesJson = string.IsNullOrWhiteSpace(featuresJson) ? "[]" : featuresJson;
        OwnPosition = ownPosition;
        OwnUrl = string.IsNullOrWhiteSpace(ownUrl) ? null : ownUrl.Trim();
        ProviderPayloadHash = providerPayloadHash;
        CapturedAt = at;
        UpdatedAt = at;
    }

    /// <summary>
    /// Records that the SERP could not be captured. Counters reset to null, never
    /// to zero: an unknown rank is not a rank of zero.
    /// </summary>
    public void RecordUnavailable(SerpAvailability availability, string note, DateTimeOffset at)
    {
        if (availability is not (SerpAvailability.NotConfigured or SerpAvailability.Unavailable))
            throw new ArgumentException("An unsuccessful snapshot must be NotConfigured or Unavailable.", nameof(availability));
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("A reason is required when the SERP cannot be captured.", nameof(note));

        Availability = availability;
        Note = note.Trim()[..Math.Min(note.Trim().Length, 2000)];
        ResultCount = 0;
        ResultsTruncated = false;
        OwnPosition = null;
        OwnUrl = null;
        FeaturesJson = "[]";
        ProviderPayloadHash = null;
        CapturedAt = at;
        UpdatedAt = at;
    }
}

/// <summary>A single organic result row inside a captured SERP.</summary>
public sealed class SerpResultEntry : Entity
{
    private SerpResultEntry() { }

    public SerpResultEntry(Guid snapshotId, int position, string url, string domain, string? title, string? snippet, bool isPaid, bool isOwned)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot is required.", nameof(snapshotId));
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position), "SERP positions start at 1.");
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Result URL is required.", nameof(url));

        SnapshotId = snapshotId;
        Position = position;
        Url = url.Trim()[..Math.Min(2048, url.Trim().Length)];
        Domain = domain.Trim().ToLowerInvariant()[..Math.Min(255, domain.Trim().Length)];
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(1000, title.Trim().Length)];
        Snippet = string.IsNullOrWhiteSpace(snippet) ? null : snippet.Trim()[..Math.Min(2000, snippet.Trim().Length)];
        IsPaid = isPaid;
        IsOwned = isOwned;
    }

    public Guid SnapshotId { get; private set; }
    public int Position { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string Domain { get; private set; } = string.Empty;
    public string? Title { get; private set; }
    public string? Snippet { get; private set; }
    public bool IsPaid { get; private set; }

    /// <summary>True when this result belongs to the tracked project's own host.</summary>
    public bool IsOwned { get; private set; }
}

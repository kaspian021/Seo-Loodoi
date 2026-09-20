using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

/// <summary>
/// How trustworthy a persisted backlink snapshot is.
/// <para>
/// These states are deliberately distinct. Collapsing "no provider configured"
/// and "provider failed" into an all-zero snapshot is exactly how invented link
/// data enters a report, so the platform stores the reason instead of a number.
/// </para>
/// </summary>
public enum BacklinkAvailability
{
    /// <summary>No backlink provider is configured. Nothing may be reported as data.</summary>
    NotConfigured = 0,

    /// <summary>A provider is configured but could not answer for this target (auth, quota, upstream failure).</summary>
    Unavailable = 1,

    /// <summary>The provider answered but the returned set is truncated or incomplete.</summary>
    Partial = 2,

    /// <summary>The provider answered with a complete set for the requested window.</summary>
    Available = 3
}

public enum BacklinkRelAttribute
{
    Unknown = 0,
    Follow = 1,
    NoFollow = 2,
    Sponsored = 3,
    Ugc = 4
}

/// <summary>
/// Aggregate counters exactly as reported by an external provider.
/// <para>
/// Every member is nullable on purpose: <c>null</c> means "this provider does not
/// report the metric" and must never be rendered as zero. Null-preserving is what
/// keeps the platform from presenting an absence of data as a measured value.
/// </para>
/// </summary>
public sealed record BacklinkCounts(
    int? ReferringDomains = null,
    int? TotalBacklinks = null,
    int? FollowCount = null,
    int? NoFollowCount = null,
    int? NewCount = null,
    int? LostCount = null,
    decimal? AuthorityScore = null,
    string? AuthorityMetricName = null,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null);

/// <summary>
/// One provider-sourced view of the inbound links of a single target host,
/// captured at a point in time. Snapshots are immutable in spirit: a refresh
/// appends a new row so history and new/lost diffs stay reconstructable.
/// </summary>
public sealed class BacklinkSnapshot : Entity
{
    private BacklinkSnapshot() { }

    public BacklinkSnapshot(Guid projectId, string targetHost, string providerName)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.", nameof(projectId));
        if (string.IsNullOrWhiteSpace(targetHost)) throw new ArgumentException("Target host is required.", nameof(targetHost));
        if (string.IsNullOrWhiteSpace(providerName)) throw new ArgumentException("Provider name is required.", nameof(providerName));

        ProjectId = projectId;
        TargetHost = targetHost.Trim().ToLowerInvariant();
        ProviderName = providerName.Trim();
        Availability = BacklinkAvailability.NotConfigured;
    }

    public Guid ProjectId { get; private set; }
    public string TargetHost { get; private set; } = string.Empty;
    public string ProviderName { get; private set; } = string.Empty;
    public BacklinkAvailability Availability { get; private set; }
    public string? Note { get; private set; }

    public int? ReferringDomains { get; private set; }
    public int? TotalBacklinks { get; private set; }
    public int? FollowCount { get; private set; }
    public int? NoFollowCount { get; private set; }
    public int? NewCount { get; private set; }
    public int? LostCount { get; private set; }
    public decimal? AuthorityScore { get; private set; }
    public string? AuthorityMetricName { get; private set; }
    public DateOnly? PeriodStart { get; private set; }
    public DateOnly? PeriodEnd { get; private set; }

    public int ObservationCount { get; private set; }
    public bool ObservationsTruncated { get; private set; }
    public string? ProviderPayloadHash { get; private set; }
    public DateTimeOffset? FetchedAt { get; private set; }

    /// <summary>Whether this snapshot carries provider-observed link data.</summary>
    public bool HasData => Availability is BacklinkAvailability.Available or BacklinkAvailability.Partial;

    /// <summary>Records a successful provider response, complete or truncated.</summary>
    public void RecordSuccess(BacklinkAvailability availability, BacklinkCounts counts, int observationCount, bool observationsTruncated, string? providerPayloadHash, DateTimeOffset fetchedAt)
    {
        if (availability is not (BacklinkAvailability.Available or BacklinkAvailability.Partial))
            throw new ArgumentException("A successful snapshot must be Available or Partial.", nameof(availability));

        Availability = availability;
        Note = null;
        ReferringDomains = counts.ReferringDomains;
        TotalBacklinks = counts.TotalBacklinks;
        FollowCount = counts.FollowCount;
        NoFollowCount = counts.NoFollowCount;
        NewCount = counts.NewCount;
        LostCount = counts.LostCount;
        AuthorityScore = counts.AuthorityScore;
        AuthorityMetricName = counts.AuthorityMetricName is null ? null : counts.AuthorityMetricName.Trim();
        PeriodStart = counts.PeriodStart;
        PeriodEnd = counts.PeriodEnd;
        ObservationCount = Math.Max(0, observationCount);
        ObservationsTruncated = observationsTruncated;
        ProviderPayloadHash = providerPayloadHash;
        FetchedAt = fetchedAt;
        UpdatedAt = fetchedAt;
    }

    /// <summary>
    /// Records that no link data could be obtained. Every counter is reset to
    /// <c>null</c>, never to zero: an unknown count is not a measured count of zero.
    /// </summary>
    public void RecordUnavailable(BacklinkAvailability availability, string note, DateTimeOffset at)
    {
        if (availability is not (BacklinkAvailability.NotConfigured or BacklinkAvailability.Unavailable))
            throw new ArgumentException("An unsuccessful snapshot must be NotConfigured or Unavailable.", nameof(availability));
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("A reason is required when no data is available.", nameof(note));

        Availability = availability;
        Note = note.Trim()[..Math.Min(note.Trim().Length, 2000)];
        ReferringDomains = null;
        TotalBacklinks = null;
        FollowCount = null;
        NoFollowCount = null;
        NewCount = null;
        LostCount = null;
        AuthorityScore = null;
        AuthorityMetricName = null;
        PeriodStart = null;
        PeriodEnd = null;
        ObservationCount = 0;
        ObservationsTruncated = false;
        ProviderPayloadHash = null;
        FetchedAt = at;
        UpdatedAt = at;
    }
}

/// <summary>
/// A single inbound link as observed by the provider. Persisted per snapshot so
/// that two snapshots can be diffed into new/lost links without re-querying the
/// vendor.
/// </summary>
public sealed class BacklinkObservation : Entity
{
    private BacklinkObservation() { }

    public BacklinkObservation(
        Guid snapshotId,
        string sourceUrl,
        string sourceHost,
        string targetUrl,
        string? anchorText,
        BacklinkRelAttribute rel,
        DateOnly? firstSeen,
        DateOnly? lastSeen,
        bool isNew,
        bool isLost)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot is required.", nameof(snapshotId));
        if (string.IsNullOrWhiteSpace(sourceUrl)) throw new ArgumentException("Source URL is required.", nameof(sourceUrl));
        if (string.IsNullOrWhiteSpace(sourceHost)) throw new ArgumentException("Source host is required.", nameof(sourceHost));
        if (string.IsNullOrWhiteSpace(targetUrl)) throw new ArgumentException("Target URL is required.", nameof(targetUrl));

        SnapshotId = snapshotId;
        SourceUrl = sourceUrl.Trim();
        SourceHost = sourceHost.Trim().ToLowerInvariant();
        TargetUrl = targetUrl.Trim();
        AnchorText = anchorText?.Trim();
        Rel = rel;
        FirstSeen = firstSeen;
        LastSeen = lastSeen;
        IsNew = isNew;
        IsLost = isLost;
    }

    public Guid SnapshotId { get; private set; }
    public string SourceUrl { get; private set; } = string.Empty;
    public string SourceHost { get; private set; } = string.Empty;
    public string TargetUrl { get; private set; } = string.Empty;
    public string? AnchorText { get; private set; }
    public BacklinkRelAttribute Rel { get; private set; }
    public DateOnly? FirstSeen { get; private set; }
    public DateOnly? LastSeen { get; private set; }
    public bool IsNew { get; private set; }
    public bool IsLost { get; private set; }
}

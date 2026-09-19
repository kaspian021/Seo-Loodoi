using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Backlinks;

/// <summary>
/// What a specific backlink provider can actually answer. The UI and the
/// reporting layer read these flags instead of hardcoding vendor assumptions,
/// so a provider that cannot report authority metrics simply does not show one.
/// </summary>
public sealed record BacklinkProviderCapabilities(
    bool SupportsReferringDomains,
    bool SupportsAnchorText,
    bool SupportsRelAttributes,
    bool SupportsNewLost,
    bool SupportsAuthorityMetrics,
    bool SupportsHistory,
    int MaxObservationsPerRequest);

public sealed record BacklinkQuery(
    string TargetHost,
    int MaxObservations,
    DateOnly? PeriodStart = null,
    DateOnly? PeriodEnd = null);

public sealed record BacklinkObservationDto(
    string SourceUrl,
    string SourceHost,
    string TargetUrl,
    string? AnchorText,
    BacklinkRelAttribute Rel,
    DateOnly? FirstSeen,
    DateOnly? LastSeen,
    bool IsNew,
    bool IsLost);

/// <summary>
/// The result of one provider call. <see cref="Observations"/> must be empty
/// unless <see cref="Availability"/> is Available or Partial - a provider that
/// cannot answer has no links to report, not zero links.
/// </summary>
public sealed record BacklinkSnapshotResult(
    string ProviderName,
    BacklinkAvailability Availability,
    string? Note,
    int? ReferringDomains,
    int? TotalBacklinks,
    int? FollowCount,
    int? NoFollowCount,
    int? NewCount,
    int? LostCount,
    decimal? AuthorityScore,
    string? AuthorityMetricName,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    IReadOnlyList<BacklinkObservationDto> Observations,
    bool ObservationsTruncated)
{
    /// <summary>Builds an honest "nothing was observed" result with no fabricated counters.</summary>
    public static BacklinkSnapshotResult WithoutData(string providerName, BacklinkAvailability availability, string note) =>
        new(providerName, availability, note, null, null, null, null, null, null, null, null, null, null, [], false);
}

/// <summary>
/// Vendor-neutral backlink data source. Implementations live in Infrastructure;
/// the Domain and Application layers never reference a vendor SDK or type.
/// </summary>
public interface IBacklinkProvider
{
    /// <summary>Stable provider identifier, persisted on every snapshot for provenance.</summary>
    string Name { get; }

    /// <summary>False when no provider is wired up. The platform then reports NotConfigured.</summary>
    bool IsConfigured { get; }

    BacklinkProviderCapabilities Capabilities { get; }

    Task<BacklinkSnapshotResult> FetchAsync(BacklinkQuery query, CancellationToken ct);
}

public sealed record BacklinkSnapshotDto(
    Guid Id,
    string TargetHost,
    string ProviderName,
    BacklinkAvailability Availability,
    string? Note,
    int? ReferringDomains,
    int? TotalBacklinks,
    int? FollowCount,
    int? NoFollowCount,
    int? NewCount,
    int? LostCount,
    decimal? AuthorityScore,
    string? AuthorityMetricName,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    int ObservationCount,
    bool ObservationsTruncated,
    DateTimeOffset? FetchedAt,
    DateTimeOffset CreatedAt);

public sealed record BacklinkObservationPageDto(
    Guid SnapshotId,
    BacklinkAvailability Availability,
    IReadOnlyList<BacklinkObservationDto> Observations,
    bool Truncated);

public sealed record BacklinkDiffDto(
    Guid FromSnapshotId,
    Guid ToSnapshotId,
    IReadOnlyList<BacklinkObservationDto> NewLinks,
    IReadOnlyList<BacklinkObservationDto> LostLinks,
    int? ReferringDomainDelta,
    int? TotalBacklinkDelta);

/// <summary>Capabilities of the currently wired provider, exposed to the UI.</summary>
public sealed record BacklinkProviderStatusDto(
    string ProviderName,
    bool IsConfigured,
    BacklinkProviderCapabilities Capabilities);

public interface IBacklinkService
{
    Task<BacklinkProviderStatusDto> ProviderStatusAsync(CancellationToken ct);

    Task<BacklinkSnapshotDto?> LatestAsync(Guid projectId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<BacklinkSnapshotDto>> HistoryAsync(Guid projectId, Guid userId, int limit, CancellationToken ct);

    /// <summary>Enqueues a durable refresh job. Returns the pending snapshot row.</summary>
    Task<BacklinkSnapshotDto?> RequestRefreshAsync(Guid projectId, Guid userId, CancellationToken ct);

    Task<BacklinkObservationPageDto?> ObservationsAsync(Guid projectId, Guid snapshotId, Guid userId, int take, CancellationToken ct);

    Task<BacklinkDiffDto?> CompareAsync(Guid projectId, Guid userId, Guid fromSnapshotId, Guid toSnapshotId, CancellationToken ct);
}

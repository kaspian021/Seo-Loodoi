using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Serp;

/// <summary>
/// What a specific SERP provider can actually return. The UI reads these flags
/// rather than assuming every vendor supports every surface.
/// </summary>
public sealed record SerpProviderCapabilities(
    bool SupportsOrganicResults,
    bool SupportsPaidResults,
    bool SupportsFeatures,
    bool SupportsAiSurfaces,
    bool SupportsLocalPack,
    bool SupportsMobileDevice,
    bool SupportsCountrySelection,
    int MaxResultsPerQuery);

public sealed record SerpQuery(
    string Phrase,
    string Country,
    string Language,
    SerpDevice Device,
    SerpSurface Surface,
    int MaxResults);

public sealed record SerpResultDto(
    int Position,
    string Url,
    string Domain,
    string? Title,
    string? Snippet,
    bool IsPaid);

public sealed record SerpFeatureDto(SerpFeatureKind Kind, int? Position, string? Detail);

/// <summary>
/// One provider call result. <see cref="Results"/> must be empty unless
/// <see cref="Availability"/> is Available or Partial.
/// </summary>
public sealed record SerpSnapshotResult(
    string ProviderName,
    SerpAvailability Availability,
    string? Note,
    IReadOnlyList<SerpResultDto> Results,
    IReadOnlyList<SerpFeatureDto> Features,
    bool ResultsTruncated)
{
    public static SerpSnapshotResult WithoutData(string providerName, SerpAvailability availability, string note) =>
        new(providerName, availability, note, [], [], false);
}

/// <summary>
/// Vendor-neutral SERP data source. The Domain and Application layers reference
/// no vendor SDK; only Infrastructure knows which provider is wired up.
/// </summary>
public interface ISerpProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    SerpProviderCapabilities Capabilities { get; }
    Task<SerpSnapshotResult> FetchAsync(SerpQuery query, CancellationToken ct);
}

public sealed record SerpSnapshotDto(
    Guid Id,
    Guid ProjectId,
    Guid? KeywordId,
    string Phrase,
    string Country,
    string Language,
    SerpDevice Device,
    SerpSurface Surface,
    string ProviderName,
    SerpAvailability Availability,
    string? Note,
    int ResultCount,
    bool ResultsTruncated,
    int? OwnPosition,
    string? OwnUrl,
    IReadOnlyList<SerpFeatureDto> Features,
    DateTimeOffset? CapturedAt,
    DateTimeOffset CreatedAt);

public sealed record SerpResultsPageDto(
    Guid SnapshotId,
    SerpAvailability Availability,
    IReadOnlyList<SerpResultDto> Results,
    bool Truncated);

public sealed record SerpMovementDto(
    string Url,
    string Domain,
    int? FromPosition,
    int? ToPosition,
    int? Delta,
    string Movement,
    bool IsOwned);

public sealed record SerpComparisonDto(
    Guid FromSnapshotId,
    Guid ToSnapshotId,
    int? FromPosition,
    int? ToPosition,
    int? PositionDelta,
    string OwnRankMovement,
    IReadOnlyList<SerpMovementDto> Movements,
    IReadOnlyList<SerpResultDto> Entered,
    IReadOnlyList<SerpResultDto> DroppedOut,
    IReadOnlyList<string> FeaturesAdded,
    IReadOnlyList<string> FeaturesRemoved);

public sealed record SerpProviderStatusDto(
    string ProviderName,
    bool IsConfigured,
    SerpProviderCapabilities Capabilities);

public interface ISerpService
{
    Task<SerpProviderStatusDto> ProviderStatusAsync(CancellationToken ct);

    Task<SerpSnapshotDto?> LatestAsync(Guid projectId, Guid keywordId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<SerpSnapshotDto>> HistoryAsync(Guid projectId, Guid keywordId, Guid userId, int limit, CancellationToken ct);

    /// <summary>Enqueues a durable capture job; returns the pending snapshot row.</summary>
    Task<SerpSnapshotDto?> RequestRefreshAsync(Guid projectId, Guid keywordId, Guid userId, SerpDevice? device, SerpSurface? surface, CancellationToken ct);

    Task<SerpResultsPageDto?> ResultsAsync(Guid projectId, Guid snapshotId, Guid userId, int take, CancellationToken ct);

    Task<SerpComparisonDto?> CompareAsync(Guid projectId, Guid userId, Guid fromSnapshotId, Guid toSnapshotId, CancellationToken ct);
}

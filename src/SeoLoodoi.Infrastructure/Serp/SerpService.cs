using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Serp;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Serp;

public sealed class SerpService(
    AppDbContext db,
    IProjectAccessService access,
    ISerpProvider provider,
    IEntitlementService entitlements,
    ISeoJobQueue jobs,
    IAuditLogService audit) : ISerpService
{
    private const int MaxHistoryRows = 60;
    private const int MaxResultPage = 200;

    public Task<SerpProviderStatusDto> ProviderStatusAsync(CancellationToken ct) =>
        Task.FromResult(new SerpProviderStatusDto(provider.Name, provider.IsConfigured, provider.Capabilities));

    public async Task<SerpSnapshotDto?> LatestAsync(Guid projectId, Guid keywordId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        var snapshot = await db.SerpSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeywordId == keywordId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return snapshot is null ? null : ToDto(snapshot);
    }

    public async Task<IReadOnlyList<SerpSnapshotDto>> HistoryAsync(Guid projectId, Guid keywordId, Guid userId, int limit, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        var take = Math.Clamp(limit, 1, MaxHistoryRows);
        var rows = await db.SerpSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeywordId == keywordId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToArray();
    }

    public async Task<SerpSnapshotDto?> RequestRefreshAsync(
        Guid projectId,
        Guid keywordId,
        Guid userId,
        SerpDevice? device,
        SerpSurface? surface,
        CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;

        var project = await db.SeoProjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (project is null) return null;
        var keyword = await db.Keywords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == keywordId && x.ProjectId == projectId, ct);
        if (keyword is null) return null;

        var entitlement = await entitlements.GetEntitlementsAsync(userId, ct);
        if (!entitlement.IsActive)
            throw new InvalidOperationException("اشتراک فعالی برای این حساب یافت نشد. برای ثبت جایگاه در SERP اشتراک خود را فعال کنید.");

        var requestedDevice = device ?? SerpDevice.Desktop;
        var requestedSurface = surface ?? SerpSurface.Organic;

        // Idempotent: an already-queued capture for the same keyword/segment is
        // returned instead of stacking duplicate snapshots and duplicate jobs.
        var pending = await db.SerpSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.KeywordId == keywordId
                        && x.CapturedAt == null && x.Device == requestedDevice && x.Surface == requestedSurface)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (pending is not null) return ToDto(pending);

        var snapshot = new SerpSnapshot(projectId, keywordId, keyword.Phrase, keyword.NormalizedPhrase,
            keyword.Country, keyword.Language, requestedDevice, requestedSurface, provider.Name);
        db.SerpSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        var payload = new SerpRefreshJobPayload(
            snapshot.Id, projectId, keywordId, keyword.Phrase, keyword.NormalizedPhrase,
            keyword.Country, keyword.Language, requestedDevice, requestedSurface, project.NormalizedHost);
        await jobs.EnqueueOnceAsync(SeoJobType.SerpRefresh, $"serp-refresh:{snapshot.Id}", JsonSerializer.Serialize(payload), ct);
        await audit.RecordAsync(projectId, userId, "SERP_REFRESH_REQUESTED", "SerpSnapshot", snapshot.Id.ToString(),
            JsonSerializer.Serialize(new { keywordId, device = requestedDevice.ToString(), surface = requestedSurface.ToString() }), null, ct);

        return ToDto(snapshot);
    }

    public async Task<SerpResultsPageDto?> ResultsAsync(Guid projectId, Guid snapshotId, Guid userId, int take, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;

        var snapshot = await db.SerpSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == snapshotId && x.ProjectId == projectId, ct);
        if (snapshot is null) return null;

        // No provider data means no results to list - not "this keyword ranks nowhere".
        if (!snapshot.HasData) return new SerpResultsPageDto(snapshot.Id, snapshot.Availability, [], false);

        var limit = Math.Clamp(take, 1, MaxResultPage);
        var rows = await db.SerpResultEntries.AsNoTracking()
            .Where(x => x.SnapshotId == snapshotId)
            .OrderBy(x => x.Position)
            .Take(limit)
            .ToListAsync(ct);

        var truncated = snapshot.ResultsTruncated || (rows.Count == limit && snapshot.ResultCount > limit);
        return new SerpResultsPageDto(
            snapshot.Id,
            snapshot.Availability,
            rows.Select(x => new SerpResultDto(x.Position, x.Url, x.Domain, x.Title, x.Snippet, x.IsPaid)).ToArray(),
            truncated);
    }

    public async Task<SerpComparisonDto?> CompareAsync(Guid projectId, Guid userId, Guid fromSnapshotId, Guid toSnapshotId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;

        var ids = new[] { fromSnapshotId, toSnapshotId };
        var snapshots = await db.SerpSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && ids.Contains(x.Id))
            .ToListAsync(ct);
        var from = snapshots.SingleOrDefault(x => x.Id == fromSnapshotId);
        var to = snapshots.SingleOrDefault(x => x.Id == toSnapshotId);
        if (from is null || to is null) return null;

        // Comparing against a capture with no data would report every result as
        // "dropped out". Refuse rather than invent that conclusion.
        if (!from.HasData || !to.HasData) return null;

        var fromResults = await ResultsByUrlAsync(from.Id, ct);
        var toResults = await ResultsByUrlAsync(to.Id, ct);

        var movements = new List<SerpMovementDto>();
        foreach (var (url, current) in toResults)
        {
            if (!fromResults.TryGetValue(url, out var previous)) continue;
            var delta = previous.Position - current.Position;
            movements.Add(new SerpMovementDto(
                url, current.Domain, previous.Position, current.Position, delta,
                DescribeMovement(delta), current.IsOwned));
        }

        var entered = toResults.Where(x => !fromResults.ContainsKey(x.Key))
            .OrderBy(x => x.Value.Position)
            .Select(x => new SerpResultDto(x.Value.Position, x.Value.Url, x.Value.Domain, x.Value.Title, x.Value.Snippet, x.Value.IsPaid))
            .ToArray();
        var dropped = fromResults.Where(x => !toResults.ContainsKey(x.Key))
            .OrderBy(x => x.Value.Position)
            .Select(x => new SerpResultDto(x.Value.Position, x.Value.Url, x.Value.Domain, x.Value.Title, x.Value.Snippet, x.Value.IsPaid))
            .ToArray();

        var fromFeatures = ParseFeatures(from.FeaturesJson);
        var toFeatures = ParseFeatures(to.FeaturesJson);
        var added = toFeatures.Where(f => !fromFeatures.Contains(f)).ToArray();
        var removed = fromFeatures.Where(f => !toFeatures.Contains(f)).ToArray();

        // A negative delta means the position number went down, i.e. the page ranked higher.
        int? positionDelta = from.OwnPosition is { } fp && to.OwnPosition is { } tp ? fp - tp : null;

        return new SerpComparisonDto(
            from.Id, to.Id, from.OwnPosition, to.OwnPosition, positionDelta,
            DescribeMovement(positionDelta),
            movements.OrderByDescending(x => Math.Abs(x.Delta ?? 0)).ToArray(),
            entered, dropped, added, removed);
    }

    private async Task<Dictionary<string, SerpResultEntry>> ResultsByUrlAsync(Guid snapshotId, CancellationToken ct)
    {
        var rows = await db.SerpResultEntries.AsNoTracking().Where(x => x.SnapshotId == snapshotId).ToListAsync(ct);
        var index = new Dictionary<string, SerpResultEntry>(StringComparer.Ordinal);
        foreach (var row in rows) index[NormalizeUrl(row.Url)] = row;
        return index;
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        return $"{host}{path}";
    }

    private static string DescribeMovement(int? delta) => delta switch
    {
        null => "unknown",
        > 0 => "improved",
        < 0 => "declined",
        _ => "unchanged"
    };

    private static IReadOnlyList<string> ParseFeatures(string json)
    {
        try
        {
            var features = JsonSerializer.Deserialize<List<SerpFeatureDto>>(json);
            return features is null ? [] : features.Select(f => f.Kind.ToString()).Distinct().ToArray();
        }
        catch (JsonException)
        {
            // Malformed provider payloads must not break comparison; an empty
            // feature list is honest about what could be read back.
            return [];
        }
    }

    private static SerpSnapshotDto ToDto(SerpSnapshot s)
    {
        IReadOnlyList<SerpFeatureDto> features;
        try { features = JsonSerializer.Deserialize<List<SerpFeatureDto>>(s.FeaturesJson) ?? []; }
        catch (JsonException) { features = []; }

        return new SerpSnapshotDto(
            s.Id, s.ProjectId, s.KeywordId, s.Phrase, s.Country, s.Language,
            s.Device, s.Surface, s.ProviderName, s.Availability, s.Note,
            s.ResultCount, s.ResultsTruncated, s.OwnPosition, s.OwnUrl,
            features, s.CapturedAt, s.CreatedAt);
    }
}

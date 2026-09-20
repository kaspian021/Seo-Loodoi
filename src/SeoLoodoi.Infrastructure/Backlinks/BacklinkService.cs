using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Backlinks;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Backlinks;

public sealed class BacklinkService(
    AppDbContext db,
    IProjectAccessService access,
    IBacklinkProvider provider,
    IEntitlementService entitlements,
    ISeoJobQueue jobs,
    IAuditLogService audit) : IBacklinkService
{
    private const int MaxHistoryRows = 60;
    private const int MaxObservationPage = 500;

    public Task<BacklinkProviderStatusDto> ProviderStatusAsync(CancellationToken ct) =>
        Task.FromResult(new BacklinkProviderStatusDto(provider.Name, provider.IsConfigured, provider.Capabilities));

    public async Task<BacklinkSnapshotDto?> LatestAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;
        var snapshot = await db.BacklinkSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return snapshot is null ? null : ToDto(snapshot);
    }

    public async Task<IReadOnlyList<BacklinkSnapshotDto>> HistoryAsync(Guid projectId, Guid userId, int limit, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return [];
        var take = Math.Clamp(limit, 1, MaxHistoryRows);
        var rows = await db.BacklinkSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToArray();
    }

    public async Task<BacklinkSnapshotDto?> RequestRefreshAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, userId, ct)) return null;

        var project = await db.SeoProjects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (project is null) return null;

        var entitlement = await entitlements.GetEntitlementsAsync(userId, ct);
        if (!entitlement.IsActive)
            throw new InvalidOperationException("اشتراک فعالی برای این حساب یافت نشد. برای دریافت داده بک‌لینک اشتراک خود را فعال کنید.");

        // Idempotent: an already-queued refresh is returned instead of stacking
        // duplicate snapshot rows and duplicate jobs for the same project.
        var pending = await db.BacklinkSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.FetchedAt == null)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (pending is not null) return ToDto(pending);

        var snapshot = new BacklinkSnapshot(projectId, project.NormalizedHost, provider.Name);
        db.BacklinkSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);

        var payload = new BacklinkRefreshJobPayload(snapshot.Id, projectId, project.NormalizedHost);
        await jobs.EnqueueOnceAsync(SeoJobType.BacklinkRefresh, $"backlink-refresh:{snapshot.Id}", JsonSerializer.Serialize(payload), ct);
        await audit.RecordAsync(projectId, userId, "BACKLINK_REFRESH_REQUESTED", "BacklinkSnapshot", snapshot.Id.ToString(),
            JsonSerializer.Serialize(new { targetHost = project.NormalizedHost, provider = provider.Name }), null, ct);

        return ToDto(snapshot);
    }

    public async Task<BacklinkObservationPageDto?> ObservationsAsync(Guid projectId, Guid snapshotId, Guid userId, int take, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;

        var snapshot = await db.BacklinkSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == snapshotId && x.ProjectId == projectId, ct);
        if (snapshot is null) return null;

        // A snapshot without provider data has no links to list - not zero links.
        if (!snapshot.HasData) return new BacklinkObservationPageDto(snapshot.Id, snapshot.Availability, [], false);

        var limit = Math.Clamp(take, 1, MaxObservationPage);
        var rows = await db.BacklinkObservations.AsNoTracking()
            .Where(x => x.SnapshotId == snapshotId)
            .OrderByDescending(x => x.IsNew)
            .ThenBy(x => x.SourceHost)
            .Take(limit)
            .ToListAsync(ct);

        var truncated = snapshot.ObservationsTruncated || (rows.Count == limit && snapshot.ObservationCount > limit);
        return new BacklinkObservationPageDto(
            snapshot.Id,
            snapshot.Availability,
            rows.Select(ToObservationDto).ToArray(),
            truncated);
    }

    public async Task<BacklinkDiffDto?> CompareAsync(Guid projectId, Guid userId, Guid fromSnapshotId, Guid toSnapshotId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, userId, ct)) return null;

        var ids = new[] { fromSnapshotId, toSnapshotId };
        var snapshots = await db.BacklinkSnapshots.AsNoTracking()
            .Where(x => x.ProjectId == projectId && ids.Contains(x.Id))
            .ToListAsync(ct);
        var from = snapshots.SingleOrDefault(x => x.Id == fromSnapshotId);
        var to = snapshots.SingleOrDefault(x => x.Id == toSnapshotId);
        if (from is null || to is null) return null;

        // Diffing against a snapshot that carries no provider data would report
        // every known link as "lost" - a fabricated fact. Refuse instead: an
        // honest "cannot compare" beats a confident, wrong answer.
        if (!from.HasData || !to.HasData) return null;

        var fromKeys = await ObservationIndexAsync(from.Id, ct);
        var toKeys = await ObservationIndexAsync(to.Id, ct);

        var added = toKeys.Where(x => !fromKeys.ContainsKey(x.Key)).Select(x => x.Value).ToArray();
        var removed = fromKeys.Where(x => !toKeys.ContainsKey(x.Key)).Select(x => x.Value).ToArray();

        // Deltas stay null unless both snapshots actually measured the metric.
        int? referringDelta = from.ReferringDomains is { } f && to.ReferringDomains is { } t ? t - f : null;
        int? totalDelta = from.TotalBacklinks is { } fb && to.TotalBacklinks is { } tb ? tb - fb : null;

        return new BacklinkDiffDto(from.Id, to.Id, added, removed, referringDelta, totalDelta);
    }

    private async Task<Dictionary<string, BacklinkObservationDto>> ObservationIndexAsync(Guid snapshotId, CancellationToken ct)
    {
        var rows = await db.BacklinkObservations.AsNoTracking()
            .Where(x => x.SnapshotId == snapshotId)
            .Select(x => new { x.SourceUrl, x.SourceHost, x.TargetUrl, x.AnchorText, x.Rel, x.FirstSeen, x.LastSeen, x.IsNew, x.IsLost })
            .ToListAsync(ct);

        var index = new Dictionary<string, BacklinkObservationDto>(StringComparer.Ordinal);
        foreach (var x in rows)
        {
            var dto = new BacklinkObservationDto(x.SourceUrl, x.SourceHost, x.TargetUrl, x.AnchorText, x.Rel, x.FirstSeen, x.LastSeen, x.IsNew, x.IsLost);
            index[$"{dto.SourceUrl.Trim().ToLowerInvariant()}|{dto.TargetUrl.Trim().ToLowerInvariant()}"] = dto;
        }
        return index;
    }

    private static BacklinkObservationDto ToObservationDto(BacklinkObservation x) =>
        new(x.SourceUrl, x.SourceHost, x.TargetUrl, x.AnchorText, x.Rel, x.FirstSeen, x.LastSeen, x.IsNew, x.IsLost);

    private static BacklinkSnapshotDto ToDto(BacklinkSnapshot s) => new(
        s.Id, s.TargetHost, s.ProviderName, s.Availability, s.Note,
        s.ReferringDomains, s.TotalBacklinks, s.FollowCount, s.NoFollowCount,
        s.NewCount, s.LostCount, s.AuthorityScore, s.AuthorityMetricName,
        s.PeriodStart, s.PeriodEnd, s.ObservationCount, s.ObservationsTruncated,
        s.FetchedAt, s.CreatedAt);
}

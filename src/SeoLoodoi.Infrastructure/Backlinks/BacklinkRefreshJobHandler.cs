using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Backlinks;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Backlinks;

public sealed record BacklinkRefreshJobPayload(Guid SnapshotId, Guid ProjectId, string TargetHost);

/// <summary>
/// Runs one backlink refresh outside the request path (architecture principle 8).
/// <para>
/// The handler is intentionally paranoid about provenance: it only ever persists
/// what the provider returned, and when the provider cannot answer it records
/// <c>NotConfigured</c>/<c>Unavailable</c> with a reason instead of an all-zero
/// snapshot that would later be read as "this site has no backlinks".
/// </para>
/// </summary>
public sealed class BacklinkRefreshJobHandler(
    AppDbContext db,
    IBacklinkProvider provider,
    IOptions<BacklinkProviderOptions> options,
    TimeProvider clock,
    ILogger<BacklinkRefreshJobHandler> logger) : ISeoJobHandler
{
    public SeoJobType Type => SeoJobType.BacklinkRefresh;

    public async Task HandleAsync(SeoBackgroundJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<BacklinkRefreshJobPayload>(job.PayloadJson)
            ?? throw new InvalidDataException("Invalid backlink refresh job payload.");

        var now = clock.GetUtcNow();
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = job.Id,
            ["SnapshotId"] = payload.SnapshotId,
            ["ProjectId"] = payload.ProjectId,
            ["TargetHost"] = payload.TargetHost
        });

        var snapshot = await db.BacklinkSnapshots.SingleOrDefaultAsync(x => x.Id == payload.SnapshotId, ct);
        if (snapshot is null)
        {
            logger.LogWarning("Backlink refresh skipped: snapshot no longer exists");
            return;
        }
        if (snapshot.FetchedAt is not null)
        {
            logger.LogInformation("Backlink refresh skipped: snapshot was already completed");
            return;
        }

        if (!provider.IsConfigured)
        {
            snapshot.RecordUnavailable(BacklinkAvailability.NotConfigured, NullBacklinkProvider.NoProviderNote, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var max = Math.Clamp(options.Value.MaxObservations, 1, 50_000);
        BacklinkSnapshotResult result;
        try
        {
            result = await provider.FetchAsync(new BacklinkQuery(payload.TargetHost, max), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception detail is logged for operators; the persisted note is a
            // stable, translatable string with no vendor internals in it.
            snapshot.RecordUnavailable(BacklinkAvailability.Unavailable, "دریافت داده از تأمین‌کننده بک‌لینک ناموفق بود. کمی بعد دوباره تلاش کنید.", now);
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Backlink provider {Provider} failed for {TargetHost}", provider.Name, payload.TargetHost);
            return;
        }

        if (result is null || result.Availability is not (BacklinkAvailability.Available or BacklinkAvailability.Partial))
        {
            var availability = result is { Availability: BacklinkAvailability.Unavailable } ? BacklinkAvailability.Unavailable : BacklinkAvailability.NotConfigured;
            var note = result is { Note: { Length: > 0 } reportedNote } ? reportedNote : "تأمین‌کننده داده بک‌لینکی برای این دامنه در دسترس قرار نداد.";
            snapshot.RecordUnavailable(availability, note, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var reported = result.Observations ?? [];
        var stored = reported.Take(max).ToArray();
        var truncated = result.ObservationsTruncated || reported.Count > max;

        snapshot.RecordSuccess(
            result.Availability,
            new BacklinkCounts(
                result.ReferringDomains, result.TotalBacklinks, result.FollowCount, result.NoFollowCount,
                result.NewCount, result.LostCount, result.AuthorityScore, result.AuthorityMetricName,
                result.PeriodStart, result.PeriodEnd),
            stored.Length,
            truncated,
            ComputeHash(stored),
            now);

        foreach (var link in stored)
            db.BacklinkObservations.Add(new BacklinkObservation(
                snapshot.Id, link.SourceUrl, link.SourceHost, link.TargetUrl,
                link.AnchorText, link.Rel, link.FirstSeen, link.LastSeen, link.IsNew, link.IsLost));

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Backlink snapshot stored: {Provider} reported {Reported}, persisted {Stored}, availability {Availability}, truncated {Truncated}",
            provider.Name, reported.Count, stored.Length, result.Availability, truncated);
    }

    /// <summary>
    /// Fingerprint of the persisted link set, used to detect whether a later
    /// refresh returned an identical payload.
    /// </summary>
    private static string? ComputeHash(IReadOnlyList<BacklinkObservationDto> observations)
    {
        if (observations.Count == 0) return null;
        var builder = new StringBuilder();
        foreach (var link in observations)
            builder.Append(link.SourceUrl).Append('|').Append(link.TargetUrl).Append('|').Append(link.AnchorText ?? string.Empty).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

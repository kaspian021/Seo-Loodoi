using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Serp;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Serp;

public sealed record SerpRefreshJobPayload(
    Guid SnapshotId,
    Guid ProjectId,
    Guid? KeywordId,
    string Phrase,
    string NormalizedPhrase,
    string Country,
    string Language,
    SerpDevice Device,
    SerpSurface Surface,
    string ProjectHost);

/// <summary>
/// Captures one SERP outside the request path (architecture principle 8).
/// <para>
/// The handler only persists what the provider returned. When the provider cannot
/// answer it records <c>NotConfigured</c>/<c>Unavailable</c> with a reason rather
/// than an empty result set, because an empty SERP would be read as "this keyword
/// ranks nowhere" instead of "we could not look".
/// </para>
/// </summary>
public sealed class SerpRefreshJobHandler(
    AppDbContext db,
    ISerpProvider provider,
    IOptions<SerpProviderOptions> options,
    TimeProvider clock,
    ILogger<SerpRefreshJobHandler> logger) : ISeoJobHandler
{
    private const int MaxFeatures = 50;

    public SeoJobType Type => SeoJobType.SerpRefresh;

    public async Task HandleAsync(SeoBackgroundJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SerpRefreshJobPayload>(job.PayloadJson)
            ?? throw new InvalidDataException("Invalid SERP refresh job payload.");

        var now = clock.GetUtcNow();
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = job.Id,
            ["SnapshotId"] = payload.SnapshotId,
            ["ProjectId"] = payload.ProjectId,
            ["Phrase"] = payload.Phrase
        });

        var snapshot = await db.SerpSnapshots.SingleOrDefaultAsync(x => x.Id == payload.SnapshotId, ct);
        if (snapshot is null)
        {
            logger.LogWarning("SERP refresh skipped: snapshot no longer exists");
            return;
        }
        if (snapshot.CapturedAt is not null)
        {
            logger.LogInformation("SERP refresh skipped: snapshot was already captured");
            return;
        }

        if (!provider.IsConfigured)
        {
            snapshot.RecordUnavailable(SerpAvailability.NotConfigured, NullSerpProvider.NoProviderNote, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var max = Math.Clamp(options.Value.MaxResults, 1, 500);
        SerpSnapshotResult result;
        try
        {
            result = await provider.FetchAsync(
                new SerpQuery(payload.Phrase, payload.Country, payload.Language, payload.Device, payload.Surface, max), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Vendor detail is logged for operators; the persisted note is a stable,
            // translatable string with no vendor internals in it.
            snapshot.RecordUnavailable(SerpAvailability.Unavailable, "دریافت داده SERP از تأمین‌کننده ناموفق بود. کمی بعد دوباره تلاش کنید.", now);
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "SERP provider {Provider} failed for phrase", provider.Name);
            return;
        }

        if (result is null || result.Availability is not (SerpAvailability.Available or SerpAvailability.Partial))
        {
            var availability = result is { Availability: SerpAvailability.Unavailable } ? SerpAvailability.Unavailable : SerpAvailability.NotConfigured;
            var note = result is { Note: { Length: > 0 } reportedNote } ? reportedNote : "تأمین‌کننده نتیجه‌ای برای این عبارت در دسترس قرار نداد.";
            snapshot.RecordUnavailable(availability, note, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var reported = result.Results ?? [];
        var stored = reported
            .Where(x => x.Position > 0)
            .OrderBy(x => x.Position)
            .Take(max)
            .ToArray();
        var truncated = result.ResultsTruncated || reported.Count > max;

        // "Own position" is derived only from results actually returned here, by
        // matching the tracked project's host. It is never inferred or estimated.
        var own = stored.Where(x => MatchesHost(x.Domain, x.Url, payload.ProjectHost)).OrderBy(x => x.Position).FirstOrDefault();

        var features = (result.Features ?? []).Take(MaxFeatures).ToArray();

        snapshot.RecordSuccess(
            result.Availability,
            stored.Length,
            truncated,
            JsonSerializer.Serialize(features),
            own?.Position,
            own?.Url,
            ComputeHash(stored),
            now);

        foreach (var entry in stored)
            db.SerpResultEntries.Add(new SerpResultEntry(
                snapshot.Id, entry.Position, entry.Url, entry.Domain, entry.Title, entry.Snippet,
                entry.IsPaid, MatchesHost(entry.Domain, entry.Url, payload.ProjectHost)));

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "SERP snapshot stored: {Provider} reported {Reported}, persisted {Stored}, own position {OwnPosition}, truncated {Truncated}",
            provider.Name, reported.Count, stored.Length, own?.Position, truncated);
    }

    /// <summary>
    /// Host match for "is this our own page". Subdomains count as the same site,
    /// which is how most SEO tools treat a tracked property.
    /// </summary>
    public static bool MatchesHost(string domain, string url, string projectHost)
    {
        if (string.IsNullOrWhiteSpace(projectHost)) return false;
        var host = projectHost.Trim().ToLowerInvariant();

        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(domain)) candidate = domain.Trim().ToLowerInvariant();
        else if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) candidate = uri.IdnHost.ToLowerInvariant();

        if (string.IsNullOrEmpty(candidate)) return false;
        return candidate == host || candidate.EndsWith("." + host, StringComparison.Ordinal);
    }

    private static string? ComputeHash(IReadOnlyList<SerpResultDto> results)
    {
        if (results.Count == 0) return null;
        var builder = new StringBuilder();
        foreach (var r in results) builder.Append(r.Position).Append('|').Append(r.Url).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

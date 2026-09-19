using SeoLoodoi.Application.Backlinks;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Infrastructure.Backlinks;

/// <summary>
/// The default provider: no vendor is configured, so it reports
/// <see cref="BacklinkAvailability.NotConfigured"/> and zero observations.
/// <para>
/// This class is the guardrail for "never fabricate backlink data". A provider
/// that cannot observe links must not return plausible-looking domains, anchor
/// text or authority scores, because downstream reports would treat them as fact.
/// </para>
/// </summary>
public sealed class NullBacklinkProvider : IBacklinkProvider
{
    public const string NoProviderNote =
        "هیچ تأمین‌کننده داده بک‌لینک پیکربندی نشده است. پلتفرم داده بک‌لینک تولید یا تخمین نمی‌زند.";

    public string Name => "none";

    public bool IsConfigured => false;

    public BacklinkProviderCapabilities Capabilities => new(
        SupportsReferringDomains: false,
        SupportsAnchorText: false,
        SupportsRelAttributes: false,
        SupportsNewLost: false,
        SupportsAuthorityMetrics: false,
        SupportsHistory: false,
        MaxObservationsPerRequest: 0);

    public Task<BacklinkSnapshotResult> FetchAsync(BacklinkQuery query, CancellationToken ct) =>
        Task.FromResult(BacklinkSnapshotResult.WithoutData(Name, BacklinkAvailability.NotConfigured, NoProviderNote));
}

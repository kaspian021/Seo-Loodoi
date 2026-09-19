using SeoLoodoi.Application.Serp;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Infrastructure.Serp;

/// <summary>
/// The default SERP provider: no vendor is configured, so it reports
/// <see cref="SerpAvailability.NotConfigured"/> with no results and no features.
/// <para>
/// This is the guardrail for "never fabricate". A provider that cannot observe a
/// SERP must not return plausible-looking rankings, because rank history would
/// then be built on invented numbers.
/// </para>
/// </summary>
public sealed class NullSerpProvider : ISerpProvider
{
    public const string NoProviderNote =
        "هیچ تأمین‌کننده داده SERP پیکربندی نشده است. پلتفرم جایگاهی تولید یا تخمین نمی‌زند.";

    public string Name => "none";

    public bool IsConfigured => false;

    public SerpProviderCapabilities Capabilities => new(
        SupportsOrganicResults: false,
        SupportsPaidResults: false,
        SupportsFeatures: false,
        SupportsAiSurfaces: false,
        SupportsLocalPack: false,
        SupportsMobileDevice: false,
        SupportsCountrySelection: false,
        MaxResultsPerQuery: 0);

    public Task<SerpSnapshotResult> FetchAsync(SerpQuery query, CancellationToken ct) =>
        Task.FromResult(SerpSnapshotResult.WithoutData(Name, SerpAvailability.NotConfigured, NoProviderNote));
}

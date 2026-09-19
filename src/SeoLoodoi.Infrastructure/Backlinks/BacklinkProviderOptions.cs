namespace SeoLoodoi.Infrastructure.Backlinks;

/// <summary>
/// Configuration for the external backlink data source.
/// <para>
/// SEO Loodoi never estimates, infers or samples backlinks itself. When no
/// vendor is wired up the platform reports <c>NotConfigured</c>; it does not fall
/// back to crawled outbound links, because those are not backlink data.
/// </para>
/// </summary>
public sealed class BacklinkProviderOptions
{
    /// <summary>Provider identifier persisted on every snapshot. "none" keeps the feature off.</summary>
    public string Provider { get; set; } = "none";

    /// <summary>Master switch. Credentials alone never enable a provider.</summary>
    public bool Enabled { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Upper bound on individual links persisted per snapshot (PHASE 24 bounding).</summary>
    public int MaxObservations { get; set; } = 1000;

    public int TimeoutSeconds { get; set; } = 30;
}

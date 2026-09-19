namespace SeoLoodoi.Infrastructure.Serp;

/// <summary>
/// Configuration for the external SERP data source.
/// <para>
/// Rank tracking depends entirely on real observations. When no vendor is wired
/// up the platform reports <c>NotConfigured</c>; it does not derive positions
/// from crawled pages or guess them from impressions.
/// </para>
/// </summary>
public sealed class SerpProviderOptions
{
    public string Provider { get; set; } = "none";

    public bool Enabled { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Upper bound on organic results persisted per snapshot (PHASE 24 bounding).</summary>
    public int MaxResults { get; set; } = 100;

    public int TimeoutSeconds { get; set; } = 30;

    public string DefaultCountry { get; set; } = "IR";

    public string DefaultLanguage { get; set; } = "fa";
}

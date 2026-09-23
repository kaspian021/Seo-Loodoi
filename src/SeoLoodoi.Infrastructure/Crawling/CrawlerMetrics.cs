using System.Diagnostics.Metrics;

namespace SeoLoodoi.Infrastructure.Crawling;

/// <summary>
/// Crawler v2 metrics (D8) on the "SeoLoodoi.Crawler" meter. Consumable by
/// OpenTelemetry or dotnet-counters. Tags are low-cardinality only (outcome and
/// reason, never URLs or tenant ids). Per-crawl correlation lives in the
/// structured log scopes, which carry CrawlId, ProjectId and JobId.
/// </summary>
public static class CrawlerMetrics
{
    public const string MeterName = "SeoLoodoi.Crawler";
    private static readonly Meter Meter = new(MeterName, "2.0");
    public static readonly Counter<long> PagesFetched = Meter.CreateCounter<long>("crawler.pages.fetched", description: "Raw HTTP fetches completed, tagged by status class");
    public static readonly Counter<long> FetchErrors = Meter.CreateCounter<long>("crawler.fetch.errors", description: "Raw fetches that failed (network, size, timeout, SSRF)");
    public static readonly Counter<long> Renders = Meter.CreateCounter<long>("crawler.renders", description: "Render attempts by outcome");
    public static readonly Histogram<double> RenderDuration = Meter.CreateHistogram<double>("crawler.render.duration", unit: "ms");
    public static readonly Counter<long> RenderRejected = Meter.CreateCounter<long>("crawler.render.rejected", description: "Renders refused by backpressure or the circuit breaker");
    public static readonly Counter<long> RenderCircuitOpened = Meter.CreateCounter<long>("crawler.render.circuit_opened");
    public static readonly Counter<long> RenderBlockedRequests = Meter.CreateCounter<long>("crawler.render.blocked_requests", description: "Sub-requests blocked by the SSRF guard, budget or policy");
    public static readonly Counter<long> RenderMismatches = Meter.CreateCounter<long>("crawler.render.critical_mismatches", description: "Pages whose rendered DOM differs from raw HTML in a critical field");
    public static readonly Counter<long> HostCircuitOpened = Meter.CreateCounter<long>("crawler.host.circuit_opened");
}

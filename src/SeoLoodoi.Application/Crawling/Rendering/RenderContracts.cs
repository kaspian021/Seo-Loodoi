namespace SeoLoodoi.Application.Crawling.Rendering;

/// <summary>Viewport profile used by the renderer (D2). Values are fixed, not user supplied.</summary>
public sealed record ViewportProfile(string Name, int Width, int Height, double DeviceScaleFactor, bool IsMobile, string UserAgentSuffix)
{
    public static readonly ViewportProfile Desktop = new("desktop", 1366, 900, 1, false, "");
    public static readonly ViewportProfile Mobile = new("mobile", 412, 915, 2.625, true, " Mobile");
    public static ViewportProfile For(string? name) => string.Equals(name, "mobile", StringComparison.OrdinalIgnoreCase) ? Mobile : Desktop;
}

public sealed record RenderRequest(Uri Url, string UserAgent, ViewportProfile Viewport, int TimeoutSeconds, int MaxDomBytes, Guid CrawlId, Guid ProjectId);

/// <summary>Metadata for one sub-resource the page requested while rendering (D4). No bodies are stored.</summary>
public sealed record RenderedResource(string Url, string ResourceType, int? Status, long? SizeBytes, bool Blocked, string? BlockReason);

public enum RenderFailureKind { None, Timeout, Crash, Oversized, NavigationBlocked, NavigationFailed }

public sealed record RenderResult(
    bool Success,
    RenderFailureKind Failure,
    string? FailureMessage,
    Uri? FinalUrl,
    string? Html,
    IReadOnlyList<RenderedResource> Resources,
    int JsErrorCount,
    TimeSpan Duration)
{
    public static RenderResult Failed(RenderFailureKind kind, string message, TimeSpan duration, IReadOnlyList<RenderedResource>? resources = null) =>
        new(false, kind, message, null, null, resources ?? [], 0, duration);
}

/// <summary>Thrown when the renderer cannot accept work (bounded queue full or circuit open). Callers fall back to raw HTML.</summary>
public sealed class RenderCapacityException(string message) : Exception(message);

/// <summary>
/// Headless rendering of one URL. Implementations MUST route every request the page
/// makes (document, redirects, sub-resources) through the SSRF guard, and must isolate
/// each render in its own browser context.
/// </summary>
public interface IPageRenderer
{
    /// <summary>False when rendering is disabled or the browser is unavailable in this deployment.</summary>
    bool IsAvailable { get; }
    Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct);
}

/// <summary>Deployment default when rendering is not configured: never renders, never pretends to.</summary>
public sealed class DisabledPageRenderer : IPageRenderer
{
    public bool IsAvailable => false;
    public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken ct) =>
        Task.FromResult(RenderResult.Failed(RenderFailureKind.NavigationFailed, "Rendering is disabled in this deployment.", TimeSpan.Zero));
}

/// <summary>Monthly render allowance per plan (D9). Inactive subscriptions get none.</summary>
public static class RenderQuotaPolicy
{
    public static int MonthlyRenders(string plan, bool isActive) => !isActive ? 0 : plan switch
    {
        "Free" => 25,
        "Starter" => 250,
        "Pro" => 2_500,
        "Enterprise" => 25_000,
        _ => 0
    };
}

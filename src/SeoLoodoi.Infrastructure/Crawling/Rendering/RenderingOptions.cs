namespace SeoLoodoi.Infrastructure.Crawling.Rendering;

/// <summary>Deployment configuration for JavaScript rendering, bound from the "Rendering" section.</summary>
public sealed class RenderingOptions
{
    /// <summary>Off by default: a deployment must opt in and provision Chromium. When off, render modes fall back to raw HTML and record "Disabled" evidence.</summary>
    public bool Enabled { get; set; }
    /// <summary>Optional Chromium executable. When empty, the Playwright-managed browser is used.</summary>
    public string? ExecutablePath { get; set; }
    /// <summary>Global cap on simultaneous renders in this process.</summary>
    public int MaxConcurrentRenders { get; set; } = 2;
    /// <summary>Per-project cap, so one tenant cannot hold every render slot.</summary>
    public int MaxConcurrentRendersPerProject { get; set; } = 1;
    /// <summary>Bounded wait queue. When full, new renders are rejected immediately (backpressure) and the raw HTML is used.</summary>
    public int MaxQueuedRenders { get; set; } = 16;
    public int MaxDomBytes { get; set; } = 5_000_000;
    public int MaxSubresourceRequests { get; set; } = 150;
    public long MaxSubresourceBytesPerRender { get; set; } = 25_000_000;
    public int MaxSubresourceBytes { get; set; } = 5_000_000;
    /// <summary>The browser is relaunched after this many renders, bounding memory growth.</summary>
    public int RecycleBrowserAfterRenders { get; set; } = 100;
    public int JsHeapMegabytes { get; set; } = 512;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitOpenSeconds { get; set; } = 60;
    /// <summary>Upper bound on the extra network-idle wait after the load event.</summary>
    public int NetworkIdleWaitMilliseconds { get; set; } = 3_000;

    public void Validate()
    {
        if (MaxConcurrentRenders is < 1 or > 32) throw new InvalidOperationException("Rendering:MaxConcurrentRenders must be 1-32.");
        if (MaxConcurrentRendersPerProject < 1 || MaxConcurrentRendersPerProject > MaxConcurrentRenders) throw new InvalidOperationException("Rendering:MaxConcurrentRendersPerProject must be 1..MaxConcurrentRenders.");
        if (MaxQueuedRenders is < 0 or > 1_000) throw new InvalidOperationException("Rendering:MaxQueuedRenders must be 0-1000.");
        if (MaxDomBytes is < 100_000 or > 50_000_000) throw new InvalidOperationException("Rendering:MaxDomBytes must be 100KB-50MB.");
        if (MaxSubresourceRequests is < 0 or > 1_000) throw new InvalidOperationException("Rendering:MaxSubresourceRequests must be 0-1000.");
        if (CircuitFailureThreshold < 1 || CircuitOpenSeconds < 1) throw new InvalidOperationException("Rendering circuit settings must be positive.");
        if (RecycleBrowserAfterRenders < 1) throw new InvalidOperationException("Rendering:RecycleBrowserAfterRenders must be positive.");
    }
}

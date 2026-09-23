namespace SeoLoodoi.Domain.Seo;

public sealed record CrawlSettings(
    int MaxPages = 500,
    int MaxDepth = 8,
    int Concurrency = 4,
    int DelayMilliseconds = 300,
    int TimeoutSeconds = 20,
    int RetryCount = 2,
    bool ObeyRobots = true,
    bool FollowRedirects = true,
    bool IncludeSubdomains = false,
    int MaxResponseBytes = 5_000_000,
    string UserAgent = "SEO-LoodoiBot/1.0",
    string Schedule = "off",
    int ScheduleHourUtc = 3,
    // Crawler v2 (D1). All nullable so settings persisted before v2 (JSON column)
    // materialize unchanged; null means the pre-v2 behaviour.
    string? RenderMode = null,
    string? DiscoveryMode = null,
    string? Viewport = null,
    int? MaxRendersPerCrawl = null,
    string? UrlList = null)
{
    public const int MaxUrlListEntries = 1000;
    public const int MaxUrlListChars = 200_000;

    /// <summary>html (never render, default) | js (render every HTML page) | auto (HTML-first, render only when deterministic triggers fire).</summary>
    public string EffectiveRenderMode => RenderMode ?? "html";
    /// <summary>hybrid (seed + sitemap + links, default) | spider (seed + links) | sitemap (sitemap URLs only) | list (explicit URL list only).</summary>
    public string EffectiveDiscoveryMode => DiscoveryMode ?? "hybrid";
    public string EffectiveViewport => Viewport ?? "desktop";
    public int EffectiveMaxRendersPerCrawl => MaxRendersPerCrawl ?? 100;
    public bool FollowsLinks => EffectiveDiscoveryMode is "hybrid" or "spider";
    public bool UsesSitemaps => EffectiveDiscoveryMode is "hybrid" or "sitemap";

    public IReadOnlyList<Uri> ParseUrlList()
    {
        if (string.IsNullOrWhiteSpace(UrlList)) return [];
        var result = new List<Uri>();
        foreach (var line in UrlList.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException($"URL list entry is not an absolute HTTP(S) URL: {line[..Math.Min(line.Length, 100)]}", nameof(UrlList));
            result.Add(uri);
        }
        if (result.Count > MaxUrlListEntries) throw new ArgumentOutOfRangeException(nameof(UrlList), $"URL list is limited to {MaxUrlListEntries} entries.");
        return result;
    }

    public CrawlSettings Validate()
    {
        if (MaxPages is < 1 or > 50_000) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (MaxDepth is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (Concurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(Concurrency));
        if (DelayMilliseconds is < 0 or > 60_000) throw new ArgumentOutOfRangeException(nameof(DelayMilliseconds));
        if (TimeoutSeconds is < 2 or > 120) throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds));
        if (RetryCount is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(RetryCount));
        if (MaxResponseBytes is < 100_000 or > 50_000_000) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        if (string.IsNullOrWhiteSpace(UserAgent) || UserAgent.Trim().Length > 200 || UserAgent.Any(char.IsControl)) throw new ArgumentException("A valid user agent is required.", nameof(UserAgent));
        if (Schedule is not ("off" or "daily" or "weekly" or "monthly")) throw new ArgumentOutOfRangeException(nameof(Schedule));
        if (ScheduleHourUtc is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(ScheduleHourUtc));
        var renderMode = RenderMode?.Trim().ToLowerInvariant();
        var discoveryMode = DiscoveryMode?.Trim().ToLowerInvariant();
        var viewport = Viewport?.Trim().ToLowerInvariant();
        if (renderMode is not (null or "html" or "js" or "auto")) throw new ArgumentOutOfRangeException(nameof(RenderMode));
        if (discoveryMode is not (null or "hybrid" or "spider" or "sitemap" or "list")) throw new ArgumentOutOfRangeException(nameof(DiscoveryMode));
        if (viewport is not (null or "desktop" or "mobile")) throw new ArgumentOutOfRangeException(nameof(Viewport));
        if (MaxRendersPerCrawl is < 0 or > 50_000) throw new ArgumentOutOfRangeException(nameof(MaxRendersPerCrawl));
        if (UrlList is { Length: > MaxUrlListChars }) throw new ArgumentOutOfRangeException(nameof(UrlList), $"URL list is limited to {MaxUrlListChars} characters.");
        var normalized = this with { UserAgent = UserAgent.Trim(), Schedule = Schedule.ToLowerInvariant(), RenderMode = renderMode, DiscoveryMode = discoveryMode, Viewport = viewport, UrlList = string.IsNullOrWhiteSpace(UrlList) ? null : UrlList.Trim() };
        var list = normalized.ParseUrlList();
        if (discoveryMode == "list" && list.Count == 0) throw new ArgumentException("URL-list discovery requires at least one URL.", nameof(UrlList));
        return normalized;
    }
}

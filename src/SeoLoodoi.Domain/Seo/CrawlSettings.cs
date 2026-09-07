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
    int MaxResponseBytes = 5_000_000)
{
    public CrawlSettings Validate()
    {
        if (MaxPages is < 1 or > 50_000) throw new ArgumentOutOfRangeException(nameof(MaxPages));
        if (MaxDepth is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (Concurrency is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(Concurrency));
        if (TimeoutSeconds is < 2 or > 120) throw new ArgumentOutOfRangeException(nameof(TimeoutSeconds));
        return this;
    }
}

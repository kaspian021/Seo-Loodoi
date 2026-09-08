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
    int ScheduleHourUtc = 3)
{
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
        return this with { UserAgent = UserAgent.Trim(), Schedule = Schedule.ToLowerInvariant() };
    }
}

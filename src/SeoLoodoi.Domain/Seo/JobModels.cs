using SeoLoodoi.Domain.Common;

namespace SeoLoodoi.Domain.Seo;

public sealed class CrawlFrontierItem : Entity
{
    private CrawlFrontierItem() { }
    public CrawlFrontierItem(Guid crawlId, Guid projectId, string url, string normalizedUrl, int depth, Guid? discoveredFromId = null)
    {
        if (crawlId == Guid.Empty || projectId == Guid.Empty) throw new ArgumentException("Crawl and project are required.");
        CrawlId = crawlId; ProjectId = projectId; Url = url; NormalizedUrl = normalizedUrl; Depth = depth; DiscoveredFromId = discoveredFromId;
    }
    public Guid CrawlId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string NormalizedUrl { get; private set; } = string.Empty;
    public int Depth { get; private set; }
    public Guid? DiscoveredFromId { get; private set; }
    public FrontierStatus Status { get; private set; } = FrontierStatus.Pending;
    public int Attempts { get; private set; }
    public DateTimeOffset? NotBefore { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public string? LastError { get; private set; }

    public bool TryLease(string workerId, DateTimeOffset now, TimeSpan duration)
    {
        if (Status == FrontierStatus.Leased && LeaseExpiresAt <= now) Status = FrontierStatus.Pending;
        if (Status != FrontierStatus.Pending || NotBefore > now) return false;
        Status = FrontierStatus.Leased; LeaseOwner = workerId; LeaseExpiresAt = now.Add(duration); Attempts++; UpdatedAt = now; return true;
    }
    public void Complete(DateTimeOffset now) { RequireLease(); Status = FrontierStatus.Completed; ClearLease(); UpdatedAt = now; }
    public void Skip(DateTimeOffset now, string reason) { Status = FrontierStatus.Skipped; LastError = Trim(reason); ClearLease(); UpdatedAt = now; }
    public void Retry(DateTimeOffset now, TimeSpan delay, string error, int maxAttempts)
    {
        RequireLease(); LastError = Trim(error); ClearLease(); UpdatedAt = now;
        if (Attempts >= maxAttempts) Status = FrontierStatus.Failed;
        else { Status = FrontierStatus.Pending; NotBefore = now.Add(delay); }
    }
    private void RequireLease() { if (Status != FrontierStatus.Leased) throw new InvalidOperationException("Frontier item is not leased."); }
    private void ClearLease() { LeaseOwner = null; LeaseExpiresAt = null; }
    private static string Trim(string value) => value[..Math.Min(value.Length, 2000)];
}

public sealed class SeoBackgroundJob : Entity
{
    private SeoBackgroundJob() { }
    public SeoBackgroundJob(SeoJobType type, string idempotencyKey, string payloadJson = "{}")
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        Type = type; IdempotencyKey = idempotencyKey; PayloadJson = payloadJson;
    }
    public SeoJobType Type { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public SeoJobStatus Status { get; private set; } = SeoJobStatus.Queued;
    public int Attempts { get; private set; }
    public DateTimeOffset? NotBefore { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public string? LastError { get; private set; }

    public bool TryLease(string workerId, DateTimeOffset now, TimeSpan duration)
    {
        if (Status == SeoJobStatus.Running && LeaseExpiresAt <= now) Status = SeoJobStatus.Queued;
        if (Status != SeoJobStatus.Queued || NotBefore > now) return false;
        Status = SeoJobStatus.Running; LeaseOwner = workerId; LeaseExpiresAt = now.Add(duration); Attempts++; UpdatedAt = now; return true;
    }
    public void Succeed(DateTimeOffset now) { RequireRunning(); Status = SeoJobStatus.Succeeded; ClearLease(); UpdatedAt = now; }
    public void Retry(DateTimeOffset now, TimeSpan delay, string error, int maxAttempts)
    {
        RequireRunning(); LastError = error[..Math.Min(error.Length, 2000)]; ClearLease(); UpdatedAt = now;
        if (Attempts >= maxAttempts) Status = SeoJobStatus.Failed;
        else { Status = SeoJobStatus.Queued; NotBefore = now.Add(delay); }
    }
    public void Cancel(DateTimeOffset now) { if (Status == SeoJobStatus.Succeeded) return; Status = SeoJobStatus.Cancelled; ClearLease(); UpdatedAt = now; }
    private void RequireRunning() { if (Status != SeoJobStatus.Running) throw new InvalidOperationException("Job is not running."); }
    private void ClearLease() { LeaseOwner = null; LeaseExpiresAt = null; }
}

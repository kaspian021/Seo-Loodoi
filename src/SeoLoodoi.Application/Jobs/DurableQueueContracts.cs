using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Jobs;

public interface ICrawlFrontierStore
{
    Task<bool> EnqueueAsync(CrawlFrontierItem item, CancellationToken ct);
    Task<CrawlFrontierItem?> TryLeaseAsync(Guid crawlId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

public interface ISeoJobQueue
{
    Task<Guid> EnqueueOnceAsync(SeoJobType type, string idempotencyKey, string payloadJson, CancellationToken ct, DateTimeOffset? notBefore = null);
    Task<SeoBackgroundJob?> TryLeaseAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}

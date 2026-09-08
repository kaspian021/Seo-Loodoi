using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Crawling;

public interface ICrawlCommandService
{
    Task<Crawl?> StartAsync(Guid projectId, Guid ownerId, CancellationToken ct, CrawlTrigger trigger = CrawlTrigger.Manual);
    Task<bool> PauseAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct);
    Task<bool> ResumeAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct);
    Task<bool> CancelAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct);
}

using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class CrawlQueryService(AppDbContext db) : ICrawlQueryService
{
    public async Task<IReadOnlyList<CrawlSummary>> ListAsync(Guid projectId, Guid ownerId, CancellationToken ct) => await db.Crawls.AsNoTracking()
        .Where(c => c.ProjectId == projectId && db.SeoProjects.Any(p => p.Id == c.ProjectId && p.OwnerId == ownerId))
        .OrderByDescending(c => c.CreatedAt).Select(c => new CrawlSummary(c.Id, c.Status.ToString(), c.PagesDiscovered, c.PagesCrawled, c.Errors, c.StartedAt, c.FinishedAt, c.HeartbeatAt)).ToListAsync(ct);
}

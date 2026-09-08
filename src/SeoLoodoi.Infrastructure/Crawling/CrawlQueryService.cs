using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class CrawlQueryService(AppDbContext db, IProjectAccessService access) : ICrawlQueryService
{
    public async Task<IReadOnlyList<CrawlSummary>> ListAsync(Guid projectId, Guid ownerId, CancellationToken ct)
    {
        if (!await access.CanViewAsync(projectId, ownerId, ct)) return [];
        return await db.Crawls.AsNoTracking().Where(c => c.ProjectId == projectId)
            .OrderByDescending(c => c.CreatedAt).Select(c => new CrawlSummary(c.Id, c.Status.ToString(), c.PagesDiscovered, c.PagesCrawled, c.Errors, c.StartedAt, c.FinishedAt, c.HeartbeatAt)).ToListAsync(ct);
    }
}

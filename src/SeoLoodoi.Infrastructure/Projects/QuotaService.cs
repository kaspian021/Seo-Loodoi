using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Projects;

public sealed class QuotaOptions
{
    public string DefaultPlan { get; set; } = "Starter";
    public int MaxProjects { get; set; } = 3;
    public int PagesPerMonth { get; set; } = 500;
    public int MaxKeywords { get; set; } = 25;
    public int MaxCompetitors { get; set; } = 3;
}

public sealed class QuotaService(
    AppDbContext db,
    IOptions<QuotaOptions> options,
    IEntitlementService? entitlementService = null) : IQuotaService
{
    private readonly QuotaOptions _limits = options.Value;

    public async Task<QuotaStatus> GetAsync(Guid userId, CancellationToken ct)
    {
        var accountOwnerId = await FindBillingOwnerAsync(userId, ct);
        var limits = await ResolveLimitsAsync(accountOwnerId, ct);
        var periodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var periodStartUtc = new DateTimeOffset(periodStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var projectsUsed = await db.SeoProjects.CountAsync(x => x.OwnerId == accountOwnerId && x.Status != Domain.Seo.ProjectStatus.Archived, ct);
        var pagesUsed = await CountOwnerPagesAsync(accountOwnerId, periodStartUtc, ct);
        var keywordsUsed = await CountOwnerRowsAsync(db.Keywords, accountOwnerId, ct);
        var competitorsUsed = await CountOwnerRowsAsync(db.Competitors, accountOwnerId, ct);
        return new QuotaStatus(limits.Plan, limits.MaxProjects, projectsUsed, limits.PagesPerMonth, pagesUsed, limits.MaxKeywords, keywordsUsed, limits.MaxCompetitors, competitorsUsed, periodStart);
    }

    private async Task<(string Plan, int MaxProjects, int PagesPerMonth, int MaxKeywords, int MaxCompetitors)> ResolveLimitsAsync(Guid ownerId, CancellationToken ct)
    {
        if (entitlementService is not null)
        {
            try
            {
                var entitlement = await entitlementService.GetEntitlementsAsync(ownerId, ct);
                return (entitlement.Plan, entitlement.MaxProjects, entitlement.MaxPagesPerMonth, entitlement.MaxKeywords, entitlement.MaxCompetitors);
            }
            catch
            {
                // Fall back to static limits if entitlement lookup fails
            }
        }
        return (_limits.DefaultPlan, _limits.MaxProjects, _limits.PagesPerMonth, _limits.MaxKeywords, _limits.MaxCompetitors);
    }

    // Join-based counting (F6): one statement per metric, no correlated EXISTS
    // subqueries evaluated per CrawledUrls row on every crawl start.
    private Task<int> CountOwnerPagesAsync(Guid ownerId, DateTimeOffset periodStartUtc, CancellationToken ct) =>
        (from url in db.CrawledUrls
         join project in db.SeoProjects on url.ProjectId equals project.Id
         join crawl in db.Crawls on url.CrawlId equals crawl.Id
         where url.ProjectId != Guid.Empty && project.OwnerId == ownerId && crawl.StartedAt >= periodStartUtc
         select url).CountAsync(ct);

    private Task<int> CountOwnerRowsAsync<TEntity>(DbSet<TEntity> rows, Guid ownerId, CancellationToken ct) where TEntity : class =>
        (from row in rows
         join project in db.SeoProjects on EF.Property<Guid>(row, "ProjectId") equals project.Id
         where project.OwnerId == ownerId
         select row).CountAsync(ct);

    private async Task<Guid> FindBillingOwnerAsync(Guid userId, CancellationToken ct)
    {
        var owned = await db.SeoProjects.Where(x => x.OwnerId == userId).Select(x => (Guid?)x.OwnerId).FirstOrDefaultAsync(ct);
        if (owned is not null) return owned.Value;
        return await db.ProjectMembers.Where(x => x.UserId == userId).Join(db.SeoProjects, member => member.ProjectId, project => project.Id, (_, project) => (Guid?)project.OwnerId).FirstOrDefaultAsync(ct) ?? userId;
    }

    public async Task EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct)
    {
        var limits = await ResolveLimitsAsync(userId, ct);
        var used = await db.SeoProjects.CountAsync(x => x.OwnerId == userId && x.Status != Domain.Seo.ProjectStatus.Archived, ct);
        if (used >= limits.MaxProjects) throw new QuotaExceededException($"Project quota for {limits.Plan} plan has been reached ({limits.MaxProjects}).");
    }

    public async Task EnsureCanStartCrawlAsync(Guid projectId, Guid userId, int requestedPages, CancellationToken ct)
    {
        var projectOwnerId = await db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct);
        if (projectOwnerId is null) return;
        var limits = await ResolveLimitsAsync(projectOwnerId.Value, ct);
        var periodStart = new DateTimeOffset(new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var used = await CountOwnerPagesAsync(projectOwnerId.Value, periodStart, ct);
        if (used + Math.Max(1, requestedPages) > limits.PagesPerMonth) throw new QuotaExceededException($"Monthly crawl quota for {limits.Plan} plan has been reached ({limits.PagesPerMonth} pages).");
    }

    public async Task EnsureCanAddKeywordAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var ownerId = await db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct);
        if (ownerId is null) return;
        var limits = await ResolveLimitsAsync(ownerId.Value, ct);
        var used = await CountOwnerRowsAsync(db.Keywords, ownerId.Value, ct);
        if (used >= limits.MaxKeywords) throw new QuotaExceededException($"Keyword quota for {limits.Plan} plan has been reached ({limits.MaxKeywords}).");
    }

    public async Task EnsureCanAddCompetitorAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var ownerId = await db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct);
        if (ownerId is null) return;
        var limits = await ResolveLimitsAsync(ownerId.Value, ct);
        var used = await CountOwnerRowsAsync(db.Competitors, ownerId.Value, ct);
        if (used >= limits.MaxCompetitors) throw new QuotaExceededException($"Competitor quota for {limits.Plan} plan has been reached ({limits.MaxCompetitors}).");
    }
}

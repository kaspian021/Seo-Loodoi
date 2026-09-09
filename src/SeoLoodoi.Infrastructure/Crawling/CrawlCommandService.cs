using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Crawling;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Application.Urls;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Crawling;

public sealed class CrawlCommandService(AppDbContext db, ICrawlFrontierStore frontier, ISeoJobQueue jobs, IUrlNormalizer normalizer, IProjectAccessService access, IQuotaService quota, IAuditLogService audit) : ICrawlCommandService
{
    public async Task<Crawl?> StartAsync(Guid projectId, Guid ownerId, CancellationToken ct, CrawlTrigger trigger = CrawlTrigger.Manual)
    {
        var project = await db.SeoProjects.SingleOrDefaultAsync(x => x.Id == projectId && x.Status == ProjectStatus.Active, ct);
        if (project is null || !await access.CanEditAsync(projectId, ownerId, ct)) return null;
        try
        {
            return await StartTransactionallyAsync(project, ownerId, trigger, ct);
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsSerializationFailure(ex))
        {
            // PostgreSQL SSI aborted this transaction because a concurrent
            // StartAsync committed between the active-crawl check and our
            // commit (F-04). The failed work is fully rolled back, so re-run
            // the transactional core exactly once: the re-check then either
            // throws the normal "active crawl" 409 path or we win the race.
            // A second serialization failure is propagated (extremely rare:
            // would require a third concurrent start in the same window).
            db.ChangeTracker.Clear();
            return await StartTransactionallyAsync(project, ownerId, trigger, ct);
        }
    }

    private async Task<Crawl?> StartTransactionallyAsync(SeoProject project, Guid ownerId, CrawlTrigger trigger, CancellationToken ct)
    {
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsRelational()) transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // F-05: the quota check runs INSIDE the serializable transaction so
            // the read (pages used this month) and the crawl insert are one
            // atomic unit; a concurrent transaction that changes the counted
            // pages commits first, so SSI aborts us and the retry re-checks
            // against fresh data. Reservation stays whole-MaxPages by design
            // (the handoff documents "reserves against monthly pages"): a
            // crawl may consume up to its page cap, so it must be able to.
            await quota.EnsureCanStartCrawlAsync(project.Id, ownerId, project.Settings.MaxPages, ct);
            if (await db.Crawls.AnyAsync(x => x.ProjectId == project.Id && (x.Status == CrawlStatus.Queued || x.Status == CrawlStatus.Running || x.Status == CrawlStatus.Paused), ct)) throw new InvalidOperationException("An active crawl already exists.");
            var crawl = new Crawl(project.Id, trigger); db.Crawls.Add(crawl); await db.SaveChangesAsync(ct);
            var baseUri = new Uri(project.BaseUrl); var normalized = normalizer.Normalize(baseUri);
            await frontier.EnqueueAsync(new CrawlFrontierItem(crawl.Id, project.Id, baseUri.AbsoluteUri, normalized.AbsoluteUri, 0), ct);
            await jobs.EnqueueOnceAsync(SeoJobType.InitialCrawl, $"initial-crawl:{crawl.Id}", JsonSerializer.Serialize(new CrawlJobPayload(crawl.Id, project.Id)), ct);
            crawl.ReportDiscovered(); await db.SaveChangesAsync(ct);
            await audit.RecordAsync(project.Id, ownerId, trigger == CrawlTrigger.Scheduled ? "CRAWL_SCHEDULED" : "CRAWL_STARTED", "Crawl", crawl.Id.ToString(), "{}", null, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return crawl;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    public Task<bool> PauseAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct) => ChangeAsync(projectId, crawlId, ownerId, (c,n) => c.Pause(n), "CRAWL_PAUSED", null, ct);

    public Task<bool> ResumeAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct) => ChangeAsync(
        projectId, crawlId, ownerId, (c,n) => c.Start(n), "CRAWL_RESUMED",
        () => jobs.EnqueueOnceAsync(SeoJobType.ContinueCrawl, $"continue-crawl:{crawlId}:resume:{Guid.NewGuid():N}", JsonSerializer.Serialize(new CrawlJobPayload(crawlId, projectId)), ct), ct);

    public Task<bool> CancelAsync(Guid projectId, Guid crawlId, Guid ownerId, CancellationToken ct) => ChangeAsync(projectId, crawlId, ownerId, (c,n) => c.Cancel(n), "CRAWL_CANCELLED", null, ct);

    private async Task<bool> ChangeAsync(Guid projectId, Guid crawlId, Guid ownerId, Action<Crawl,DateTimeOffset> action, string auditAction, Func<Task>? afterMutation, CancellationToken ct)
    {
        if (!await access.CanEditAsync(projectId, ownerId, ct)) return false;
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsRelational()) transaction = await db.Database.BeginTransactionAsync(ct);
            var crawl = db.Database.IsRelational()
                ? await db.Crawls.FromSqlInterpolated($"SELECT * FROM loodoi.\"Crawls\" WHERE \"Id\" = {crawlId} AND \"ProjectId\" = {projectId} FOR UPDATE").SingleOrDefaultAsync(ct)
                : await db.Crawls.SingleOrDefaultAsync(c => c.Id == crawlId && c.ProjectId == projectId, ct);
            if (crawl is null) return false;
            action(crawl, DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct);
            if (afterMutation is not null) await afterMutation();
            await audit.RecordAsync(projectId, ownerId, auditAction, "Crawl", crawl.Id.ToString(), "{}", null, ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return true;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }
}

public sealed record CrawlJobPayload(Guid CrawlId, Guid ProjectId);

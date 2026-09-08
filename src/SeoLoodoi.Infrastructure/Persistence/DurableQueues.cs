using Microsoft.EntityFrameworkCore;
using SeoLoodoi.Application.Jobs;
using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Infrastructure.Persistence;

internal sealed class CrawlFrontierStore(AppDbContext db) : ICrawlFrontierStore
{
    public async Task<bool> EnqueueAsync(CrawlFrontierItem item, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            if (await db.CrawlFrontierItems.AnyAsync(x => x.CrawlId == item.CrawlId && x.NormalizedUrl == item.NormalizedUrl, ct)) return false;
            db.CrawlFrontierItems.Add(item); await db.SaveChangesAsync(ct); return true;
        }
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO loodoi.\"CrawlFrontierItems\" (\"Id\",\"CreatedAt\",\"UpdatedAt\",\"CrawlId\",\"ProjectId\",\"Url\",\"NormalizedUrl\",\"Depth\",\"DiscoveredFromId\",\"Status\",\"Attempts\") VALUES ({item.Id},{item.CreatedAt},{item.UpdatedAt},{item.CrawlId},{item.ProjectId},{item.Url},{item.NormalizedUrl},{item.Depth},{item.DiscoveredFromId},{(int)item.Status},{item.Attempts}) ON CONFLICT (\"CrawlId\",\"NormalizedUrl\") DO NOTHING", ct);
        return rows == 1;
    }
    public async Task<CrawlFrontierItem?> TryLeaseAsync(Guid crawlId, string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var candidate = await db.CrawlFrontierItems.Where(x => x.CrawlId == crawlId && (x.Status == FrontierStatus.Pending || (x.Status == FrontierStatus.Leased && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))) && (x.NotBefore == null || x.NotBefore <= now)).OrderBy(x => x.Depth).ThenBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            // A caller must never process an item it failed to lease: returning
            // it anyway used to fetch the page and then crash in Complete(),
            // failing the whole job. Detaching drops the half-applied
            // in-memory transition so a later SaveChanges cannot persist it.
            if (candidate is null || !candidate.TryLease(workerId, now, leaseDuration)) { if (candidate is not null) db.Entry(candidate).State = EntityState.Detached; return null; }
            await db.SaveChangesAsync(ct);
            return candidate;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await db.CrawlFrontierItems
            .FromSqlInterpolated($"SELECT * FROM loodoi.\"CrawlFrontierItems\" WHERE \"CrawlId\" = {crawlId} AND (\"Status\" = 0 OR (\"Status\" = 1 AND (\"LeaseExpiresAt\" IS NULL OR \"LeaseExpiresAt\" <= {now}))) AND (\"NotBefore\" IS NULL OR \"NotBefore\" <= {now}) ORDER BY \"Depth\", \"CreatedAt\" FOR UPDATE SKIP LOCKED LIMIT 1")
            .AsTracking().SingleOrDefaultAsync(ct);
        if (item is null || !item.TryLease(workerId, now, leaseDuration)) { if (item is not null) db.Entry(item).State = EntityState.Detached; await tx.CommitAsync(ct); return null; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct); return item;
    }
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

internal sealed class SeoJobQueue(AppDbContext db) : ISeoJobQueue
{
    public async Task<Guid> EnqueueOnceAsync(SeoJobType type, string idempotencyKey, string payloadJson, CancellationToken ct, DateTimeOffset? notBefore = null)
    {
        // First writer wins: when the key already exists the stored job is
        // returned untouched and its schedule is never rewritten.
        var job = new SeoBackgroundJob(type, idempotencyKey, payloadJson, notBefore);
        if (!db.Database.IsRelational())
        {
            var existing = await db.SeoBackgroundJobs.FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return existing.Id;
            db.SeoBackgroundJobs.Add(job); await db.SaveChangesAsync(ct); return job.Id;
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO loodoi.\"SeoBackgroundJobs\" (\"Id\",\"CreatedAt\",\"UpdatedAt\",\"Type\",\"IdempotencyKey\",\"PayloadJson\",\"Status\",\"Attempts\",\"NotBefore\") VALUES ({job.Id},{job.CreatedAt},{job.UpdatedAt},{(int)job.Type},{job.IdempotencyKey},{job.PayloadJson},{(int)job.Status},{job.Attempts},{job.NotBefore}) ON CONFLICT (\"IdempotencyKey\") DO NOTHING", ct);
        return await db.SeoBackgroundJobs.Where(x => x.IdempotencyKey == idempotencyKey).Select(x => x.Id).SingleAsync(ct);
    }
    public async Task<SeoBackgroundJob?> TryLeaseAsync(string workerId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            var candidate = await db.SeoBackgroundJobs.Where(x => (x.Status == SeoJobStatus.Queued || (x.Status == SeoJobStatus.Running && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))) && (x.NotBefore == null || x.NotBefore <= now)).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            // A caller must never process an item it failed to lease: returning
            // it anyway used to fetch the page and then crash in Complete(),
            // failing the whole job. Detaching drops the half-applied
            // in-memory transition so a later SaveChanges cannot persist it.
            if (candidate is null || !candidate.TryLease(workerId, now, leaseDuration)) { if (candidate is not null) db.Entry(candidate).State = EntityState.Detached; return null; }
            await db.SaveChangesAsync(ct);
            return candidate;
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.SeoBackgroundJobs
            .FromSqlInterpolated($"SELECT * FROM loodoi.\"SeoBackgroundJobs\" WHERE (\"Status\" = 0 OR (\"Status\" = 1 AND (\"LeaseExpiresAt\" IS NULL OR \"LeaseExpiresAt\" <= {now}))) AND (\"NotBefore\" IS NULL OR \"NotBefore\" <= {now}) ORDER BY \"CreatedAt\" FOR UPDATE SKIP LOCKED LIMIT 1")
            .AsTracking().SingleOrDefaultAsync(ct);
        if (job is null || !job.TryLease(workerId, now, leaseDuration)) { if (job is not null) db.Entry(job).State = EntityState.Detached; await tx.CommitAsync(ct); return null; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct); return job;
    }
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

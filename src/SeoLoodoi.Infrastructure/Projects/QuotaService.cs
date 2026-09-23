using SeoLoodoi.Application.Crawling.Rendering;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Billing;
using SeoLoodoi.Application.Projects;
using SeoLoodoi.Infrastructure.Billing;
using SeoLoodoi.Domain.Seo;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Projects;

public sealed class QuotaOptions
{
    public string DefaultPlan { get; set; } = "Starter";
    public int MaxProjects { get; set; } = 3;
    public int PagesPerMonth { get; set; } = 500;
    public int MaxKeywords { get; set; } = 25;
    public int MaxCompetitors { get; set; } = 3;
    public int MaxTeamMembers { get; set; } = 3;
}

/// <summary>
/// Server-side quota enforcement.
/// <para>
/// Race safety: every quota-consuming write runs through <see cref="WithTenantLockAsync{T}"/>,
/// which takes a PostgreSQL transaction-scoped advisory lock keyed on the billing
/// owner (<c>pg_advisory_xact_lock</c>). All consumers of one tenant serialize on
/// that lock, so "count, compare, insert" inside the callback cannot be interleaved
/// by another request for the same tenant; other tenants are unaffected. The lock
/// is released automatically on commit or rollback, so a failed insert never
/// leaks capacity. Counting uses indexed aggregate queries — no rows are loaded.
/// The InMemory provider (tests/preview) uses an equivalent in-process lock.
/// </para>
/// <para>
/// Plan changes: limits are read at the moment of consumption. A downgrade or an
/// inactive subscription never deletes existing data; it only blocks new
/// consumption above the new limit (see <see cref="EffectiveLimits"/>).
/// </para>
/// </summary>
public sealed class QuotaService(AppDbContext db, IOptions<QuotaOptions> options, IOptions<LoodoiBillingOptions>? billing = null) : IQuotaService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> InMemoryLocks = new();
    private const long AdvisoryNamespace = 0x5E0_10AD_0000_0000;
    private readonly QuotaOptions _fallback = options.Value;
    internal AppDbContext Db => db;

    public async Task<QuotaStatus> GetAsync(Guid userId, CancellationToken ct)
    {
        var ownerId = await TenantResolver.FindBillingOwnerAsync(db, userId, ct);
        var limits = await ResolveLimitsAsync(ownerId, ct);
        var periodStart = PeriodStart();
        var counts = new QuotaCounter(db, ownerId);
        return new QuotaStatus(limits.Plan, limits.Limits.MaxProjects, await counts.ProjectsAsync(ct), limits.Limits.MaxPagesPerMonth, await counts.PagesAsync(periodStart, ct),
            limits.Limits.MaxKeywords, await counts.KeywordsAsync(ct), limits.Limits.MaxCompetitors, await counts.CompetitorsAsync(ct), DateOnly.FromDateTime(periodStart.UtcDateTime),
            limits.Limits.MaxTeamMembers, await counts.TeamSeatsAsync(DateTimeOffset.UtcNow, ct), limits.IsActive,
            RenderQuotaPolicy.MonthlyRenders(limits.Plan, limits.IsActive), await counts.RendersAsync(periodStart, ct));
    }

    public async Task<T> WithTenantLockAsync<T>(Guid ownerId, Func<IQuotaLease, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Tenant is required.", nameof(ownerId));
        if (!db.Database.IsRelational())
        {
            var gate = InMemoryLocks.GetOrAdd(ownerId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try { return await work(new Lease(this, ownerId), ct); }
            finally { gate.Release(); }
        }

        IDbContextTransaction? owned = null;
        if (db.Database.CurrentTransaction is null) owned = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey(ownerId)})", ct);
            var result = await work(new Lease(this, ownerId), ct);
            if (owned is not null) await owned.CommitAsync(ct);
            return result;
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    public async Task EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct)
    {
        var lease = new Lease(this, userId);
        await lease.EnsureAvailableAsync(QuotaDimension.Projects, 1, ct);
    }

    public async Task EnsureCanStartCrawlAsync(Guid projectId, Guid userId, int requestedPages, CancellationToken ct)
    {
        var ownerId = await OwnerOfAsync(projectId, ct); if (ownerId is null) return;
        await new Lease(this, ownerId.Value).EnsureAvailableAsync(QuotaDimension.CrawlPages, Math.Max(1, requestedPages), ct);
    }

    public async Task EnsureCanAddKeywordAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var ownerId = await OwnerOfAsync(projectId, ct); if (ownerId is null) return;
        await new Lease(this, ownerId.Value).EnsureAvailableAsync(QuotaDimension.Keywords, 1, ct);
    }

    public async Task EnsureCanAddCompetitorAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var ownerId = await OwnerOfAsync(projectId, ct); if (ownerId is null) return;
        await new Lease(this, ownerId.Value).EnsureAvailableAsync(QuotaDimension.Competitors, 1, ct);
    }

    internal async Task<TenantLimits> ResolveLimitsAsync(Guid ownerId, CancellationToken ct)
    {
        var entitlement = await db.TenantEntitlements.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == ownerId, ct);
        if (entitlement is not null)
        {
            var now = DateTimeOffset.UtcNow;
            return new TenantLimits(entitlement.Plan, entitlement.IsActive(now), entitlement.Effective(now));
        }
        // No billing record yet. In the application the billing default plan
        // (Free unless configured) applies, exactly as EntitlementService would seed it,
        // so a brand-new tenant never gets paid limits. Unit tests without billing
        // options use the static QuotaOptions.
        if (billing is not null)
        {
            var planId = PlanCatalog.IsValidPlan(billing.Value.DefaultPlan) ? billing.Value.DefaultPlan : PlanCatalog.Free;
            var plan = PlanCatalog.GetPlan(planId);
            return new TenantLimits(plan.PlanId, true, new EffectiveLimits(plan.MaxProjects, plan.MaxPagesPerMonth, plan.MaxKeywords, plan.MaxCompetitors, plan.MaxTeamMembers, plan.MaxAiCreditsPerMonth));
        }
        return new TenantLimits(_fallback.DefaultPlan, true, new EffectiveLimits(_fallback.MaxProjects, _fallback.PagesPerMonth, _fallback.MaxKeywords, _fallback.MaxCompetitors, _fallback.MaxTeamMembers, 0));
    }

    private Task<Guid?> OwnerOfAsync(Guid projectId, CancellationToken ct) =>
        db.SeoProjects.Where(x => x.Id == projectId).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct);

    internal static DateTimeOffset PeriodStart()
    {
        var now = DateTime.UtcNow;
        return new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    internal static long LockKey(Guid ownerId)
    {
        Span<byte> bytes = stackalloc byte[16];
        ownerId.TryWriteBytes(bytes);
        return AdvisoryNamespace ^ BitConverter.ToInt64(bytes[..8]) ^ BitConverter.ToInt64(bytes[8..]);
    }

    private sealed class Lease(QuotaService service, Guid ownerId) : IQuotaLease
    {
        public Guid OwnerId => ownerId;

        public async Task<QuotaCheck> CheckAsync(QuotaDimension dimension, CancellationToken ct)
        {
            var limits = await service.ResolveLimitsAsync(ownerId, ct);
            var counts = new QuotaCounter(service.Db, ownerId);
            var (limit, used) = dimension switch
            {
                QuotaDimension.Projects => (limits.Limits.MaxProjects, await counts.ProjectsAsync(ct)),
                QuotaDimension.Keywords => (limits.Limits.MaxKeywords, await counts.KeywordsAsync(ct)),
                QuotaDimension.Competitors => (limits.Limits.MaxCompetitors, await counts.CompetitorsAsync(ct)),
                QuotaDimension.TeamSeats => (limits.Limits.MaxTeamMembers, await counts.TeamSeatsAsync(DateTimeOffset.UtcNow, ct)),
                QuotaDimension.CrawlPages => (limits.Limits.MaxPagesPerMonth, await counts.PagesAsync(PeriodStart(), ct)),
                QuotaDimension.Renders => (RenderQuotaPolicy.MonthlyRenders(limits.Plan, limits.IsActive), await counts.RendersAsync(PeriodStart(), ct)),
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            };
            return new QuotaCheck(dimension, limits.Plan, limit, used);
        }

        public async Task EnsureAvailableAsync(QuotaDimension dimension, int units, CancellationToken ct)
        {
            var check = await CheckAsync(dimension, ct);
            if (check.Used + Math.Max(1, units) <= check.Limit) return;
            var message = dimension switch
            {
                QuotaDimension.Projects => $"Project quota for {check.Plan} plan has been reached ({check.Limit}).",
                QuotaDimension.Keywords => $"Keyword quota for {check.Plan} plan has been reached ({check.Limit}).",
                QuotaDimension.Competitors => $"Competitor quota for {check.Plan} plan has been reached ({check.Limit}).",
                QuotaDimension.TeamSeats => $"Team seat quota for {check.Plan} plan has been reached ({check.Limit}, including the owner and pending invitations).",
                QuotaDimension.CrawlPages => $"Monthly crawl quota for {check.Plan} plan has been reached ({check.Limit} pages).",
                QuotaDimension.Renders => $"Monthly JavaScript render quota for {check.Plan} plan has been reached ({check.Limit} renders).",
                _ => "Quota reached."
            };
            throw new QuotaExceededException(message) { Dimension = dimension };
        }
    }
}

/// <summary>Aggregate usage queries for one billing owner (single COUNT statements; nothing materialized).</summary>
internal sealed class QuotaCounter(AppDbContext db, Guid ownerId)
{
    public Task<int> ProjectsAsync(CancellationToken ct) =>
        db.SeoProjects.CountAsync(x => x.OwnerId == ownerId && x.Status != ProjectStatus.Archived, ct);

    public Task<int> PagesAsync(DateTimeOffset periodStartUtc, CancellationToken ct) =>
        (from url in db.CrawledUrls
         join project in db.SeoProjects on url.ProjectId equals project.Id
         join crawl in db.Crawls on url.CrawlId equals crawl.Id
         where url.ProjectId != Guid.Empty && project.OwnerId == ownerId && crawl.StartedAt >= periodStartUtc
         select url).CountAsync(ct);

    /// <summary>Charged renders (reserved, rendered or failed) this billing month across the owner's projects.</summary>
    public Task<int> RendersAsync(DateTimeOffset periodStartUtc, CancellationToken ct) =>
        (from evidence in db.PageRenderEvidences
         join project in db.SeoProjects on evidence.ProjectId equals project.Id
         where project.OwnerId == ownerId && evidence.CreatedAt >= periodStartUtc &&
               (evidence.Status == RenderEvidenceStatus.Reserved || evidence.Status == RenderEvidenceStatus.Rendered || evidence.Status == RenderEvidenceStatus.Failed)
         select evidence).CountAsync(ct);

    public Task<int> KeywordsAsync(CancellationToken ct) =>
        (from row in db.Keywords join project in db.SeoProjects on row.ProjectId equals project.Id where project.OwnerId == ownerId select row).CountAsync(ct);

    public Task<int> CompetitorsAsync(CancellationToken ct) =>
        (from row in db.Competitors join project in db.SeoProjects on row.ProjectId equals project.Id where project.OwnerId == ownerId select row).CountAsync(ct);

    /// <summary>
    /// Seat rule (see docs/P0-AUDIT-AND-HARDENING.md §Team seats):
    /// 1 (the owner) + distinct collaborators across all of the owner's projects
    /// (Active or Suspended — a suspended member keeps the seat) + distinct open
    /// invitations (Pending and not expired) for e-mails that are not already
    /// collaborators. Removed members, cancelled/accepted/expired invitations
    /// free their seat. Members are counted once however many projects they are on.
    /// </summary>
    public async Task<int> TeamSeatsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var memberIds = from m in db.ProjectMembers
                        join project in db.SeoProjects on m.ProjectId equals project.Id
                        where project.OwnerId == ownerId && m.UserId != ownerId
                        select m.UserId;
        var members = await memberIds.Distinct().CountAsync(ct);
        var memberEmails = from id in memberIds join u in db.Users on id equals u.Id select u.NormalizedEmail;
        var pending = await (from i in db.ProjectInvitations
                             join project in db.SeoProjects on i.ProjectId equals project.Id
                             where project.OwnerId == ownerId && i.Status == ProjectInvitationStatus.Pending && i.ExpiresAt > now
                                   && !memberEmails.Contains(i.NormalizedEmail)
                             select i.NormalizedEmail).Distinct().CountAsync(ct);
        return 1 + members + pending;
    }
}

/// <summary>
/// Resolves the billing owner (tenant) for a user. A user who owns projects is
/// their own tenant; a pure collaborator is billed to the owner of the earliest
/// project they joined (deterministic ordering, see finding B9).
/// </summary>
internal static class TenantResolver
{
    public static async Task<Guid> FindBillingOwnerAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        if (await db.SeoProjects.AnyAsync(x => x.OwnerId == userId, ct)) return userId;
        return await db.ProjectMembers.Where(x => x.UserId == userId && x.Status == ProjectMemberStatus.Active)
            .Join(db.SeoProjects, m => m.ProjectId, p => p.Id, (m, p) => new { m.CreatedAt, p.OwnerId })
            .OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.OwnerId).FirstOrDefaultAsync(ct) ?? userId;
    }
}

using SeoLoodoi.Domain.Seo;

namespace SeoLoodoi.Application.Projects;

public sealed record QuotaStatus(string Plan, int MaxProjects, int ProjectsUsed, int PagesPerMonth, int PagesUsed, int MaxKeywords, int KeywordsUsed, int MaxCompetitors, int CompetitorsUsed, DateOnly PeriodStart,
    int MaxTeamMembers = 0, int TeamMembersUsed = 0, bool SubscriptionActive = true, int RendersPerMonth = 0, int RendersUsed = 0);

/// <summary>Quota dimensions enforced server-side under the tenant quota lock.</summary>
public enum QuotaDimension { Projects, Keywords, Competitors, TeamSeats, CrawlPages, Renders }

/// <summary>
/// Handle given to work running under the per-tenant quota lock. Usage counted
/// through the lease is consistent with every other consumer for the same
/// tenant, because all consumers serialize on the same lock; so
/// <c>EnsureAvailableAsync</c> followed by an insert inside the same callback is
/// atomic with respect to concurrent requests.
/// </summary>
public interface IQuotaLease
{
    Guid OwnerId { get; }
    Task<QuotaCheck> CheckAsync(QuotaDimension dimension, CancellationToken ct);
    /// <summary>Throws <see cref="QuotaExceededException"/> when fewer than <paramref name="units"/> remain.</summary>
    Task EnsureAvailableAsync(QuotaDimension dimension, int units, CancellationToken ct);
}

public sealed record QuotaCheck(QuotaDimension Dimension, string Plan, int Limit, int Used)
{
    public int Remaining => Math.Max(0, Limit - Used);
}

public interface IQuotaService
{
    Task<QuotaStatus> GetAsync(Guid userId, CancellationToken ct);
    /// <summary>
    /// Runs <paramref name="work"/> inside a transaction holding the tenant's quota
    /// lock (PostgreSQL <c>pg_advisory_xact_lock</c>). If the caller already has an
    /// open transaction it is reused, otherwise one is started and committed
    /// after <paramref name="work"/> returns; any exception rolls everything back.
    /// </summary>
    Task<T> WithTenantLockAsync<T>(Guid ownerId, Func<IQuotaLease, CancellationToken, Task<T>> work, CancellationToken ct);
    Task EnsureCanCreateProjectAsync(Guid userId, CancellationToken ct);
    Task EnsureCanStartCrawlAsync(Guid projectId, Guid userId, int requestedPages, CancellationToken ct);
    Task EnsureCanAddKeywordAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task EnsureCanAddCompetitorAsync(Guid projectId, Guid userId, CancellationToken ct);
}

public sealed class QuotaExceededException(string message) : InvalidOperationException(message)
{
    public QuotaDimension? Dimension { get; init; }
}

/// <summary>Limits resolved for a tenant at a moment in time (already downgraded when the subscription is inactive).</summary>
public sealed record TenantLimits(string Plan, bool IsActive, EffectiveLimits Limits);
